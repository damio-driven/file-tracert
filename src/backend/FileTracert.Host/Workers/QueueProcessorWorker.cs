using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Workers;

/// <summary>
/// Background service that polls for runnable <see cref="OperationJob"/> records and
/// executes them sequentially (FIFO, one at a time). Resumes in-progress jobs from
/// their last checkpoint on restart.
/// </summary>
public sealed class QueueProcessorWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ISpaceLedger _ledger;
    private readonly IJobCancellationRegistry _cancellation;
    private readonly ILogger<QueueProcessorWorker> _logger;

    public QueueProcessorWorker(
        IServiceProvider services,
        ISpaceLedger ledger,
        IJobCancellationRegistry cancellation,
        ILogger<QueueProcessorWorker> logger)
    {
        _services = services;
        _ledger = ledger;
        _cancellation = cancellation;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Restore the in-memory ledger from DB before processing any jobs.
        await _ledger.RebuildFromDbAsync(stoppingToken);
        _logger.LogInformation("QueueProcessorWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int? jobId = await PeekNextRunnableJobAsync(stoppingToken);

                if (jobId is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }

                using (var scope = _services.CreateScope())
                {
                    var engine = scope.ServiceProvider.GetRequiredService<JobExecutionEngine>();

                    // Run under a per-job token so a Cancel request from the API can interrupt this
                    // job specifically; linked to stoppingToken so shutdown still cancels it.
                    var jobToken = _cancellation.Register(jobId.Value, stoppingToken);
                    try
                    {
                        await engine.ExecuteJobAsync(jobId.Value, jobToken);
                    }
                    finally
                    {
                        _cancellation.Remove(jobId.Value);
                    }
                }

                // §4 revaluation cycle: a completed job may have materialized the space a
                // Blocked(InsufficientSpace) job is waiting for — wake those up so they
                // restart on their own (fresh scope: the engine's scope is already spent).
                if (await IsCompletedAsync(jobId.Value, stoppingToken))
                {
                    using var revalScope = _services.CreateScope();
                    await revalScope.ServiceProvider
                        .GetRequiredService<BlockedJobRevaluator>()
                        .RevaluateAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueueProcessorWorker: unhandled error in main loop.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("QueueProcessorWorker stopping.");
    }

    private async Task<bool> IsCompletedAsync(int jobId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        return await db.OperationJobs
            .Where(j => j.Id == jobId)
            .Select(j => j.State)
            .FirstOrDefaultAsync(ct) == JobState.Completed;
    }

    private async Task<int?> PeekNextRunnableJobAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();

        return await db.OperationJobs
            .Where(j => JobStates.Runnable.Contains(j.State))
            .OrderBy(j => j.SequenceOrder)
            .Select(j => (int?)j.Id)
            .FirstOrDefaultAsync(ct);
    }
}
