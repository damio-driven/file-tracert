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
    private readonly ILogger<QueueProcessorWorker> _logger;

    // Runnable states: the job is ready to execute or already in-flight from a prior run.
    private static readonly IReadOnlySet<JobState> RunnableStates = new HashSet<JobState>
    {
        JobState.Pending,
        JobState.SpaceReserved,
        JobState.Copying,
        JobState.Verifying,
        JobState.DeletingSource
    };

    public QueueProcessorWorker(
        IServiceProvider services,
        ISpaceLedger ledger,
        ILogger<QueueProcessorWorker> logger)
    {
        _services = services;
        _ledger = ledger;
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

                using var scope = _services.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<JobExecutionEngine>();
                await engine.ExecuteJobAsync(jobId.Value, stoppingToken);
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

    private async Task<int?> PeekNextRunnableJobAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();

        return await db.OperationJobs
            .Where(j => RunnableStates.Contains(j.State))
            .OrderBy(j => j.SequenceOrder)
            .Select(j => (int?)j.Id)
            .FirstOrDefaultAsync(ct);
    }
}
