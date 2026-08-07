using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Host.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Harness;

/// <summary>
/// Drives the queue exactly the way the service does: jobs go in through the real
/// <see cref="IQueueService"/> and are executed by the real <see cref="QueueProcessorWorker"/>
/// (which in turn runs the real <c>JobExecutionEngine</c>, ledger and file mover). The harness
/// only observes — it never advances a state machine itself.
///
/// Stopping the worker is how the harness simulates a service crash: the stopping token trips
/// mid-step, the engine aborts at its last checkpoint and the job stays runnable, so starting a
/// fresh worker afterwards exercises the real resume path.
/// </summary>
public sealed class QueueDriver : IAsyncDisposable
{
    private static readonly HashSet<JobState> Terminal =
        [JobState.Completed, JobState.Failed, JobState.Cancelled];

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IServiceProvider _services;
    private readonly Action<string> _log;
    private QueueProcessorWorker? _worker;

    public QueueDriver(IServiceProvider services, Action<string> log)
    {
        _services = services;
        _log = log;
    }

    /// <summary>True while a worker is running.</summary>
    public bool WorkerRunning => _worker is not null;

    public async Task StartWorkerAsync(CancellationToken ct)
    {
        if (_worker is not null) return;
        _worker = ActivatorUtilities.CreateInstance<QueueProcessorWorker>(_services);
        await _worker.StartAsync(ct);
        _log("queue worker started");
    }

    /// <summary>
    /// Stops the worker the way a service shutdown (or a crash at a checkpoint) would: the
    /// in-flight job is interrupted and left at its persisted checkpoint.
    /// </summary>
    public async Task StopWorkerAsync()
    {
        if (_worker is null) return;
        var worker = _worker;
        _worker = null;
        await worker.StopAsync(CancellationToken.None);
        worker.Dispose();
        _log("queue worker stopped");
    }

    // ── enqueue / cancel through the real service ────────────────────────────

    public async Task<OperationJobDto> EnqueueAsync(CreateJobRequest request, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IQueueService>();
        return await queue.EnqueueAsync(request, ct);
    }

    /// <summary>
    /// Enqueues and returns the exception instead of throwing, for the scenarios whose expected
    /// outcome IS a rejection (the API maps <see cref="ArgumentException"/> and
    /// <see cref="InvalidOperationException"/> to 400).
    /// </summary>
    public async Task<Exception?> TryEnqueueAsync(CreateJobRequest request, CancellationToken ct)
    {
        try
        {
            await EnqueueAsync(request, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    public async Task CancelAsync(int jobId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IQueueService>();
        await queue.CancelAsync(jobId, ct);
    }

    // ── observation ──────────────────────────────────────────────────────────

    public async Task<OperationJob> LoadJobAsync(int jobId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        return await db.OperationJobs
                   .Include(j => j.Items)
                   .AsNoTracking()
                   .FirstOrDefaultAsync(j => j.Id == jobId, ct)
               ?? throw new InvalidOperationException($"Job {jobId} disappeared from the harness database.");
    }

    /// <summary>Polls until <paramref name="predicate"/> holds, or fails the scenario on timeout.</summary>
    public async Task<OperationJob> WaitAsync(
        int jobId, Func<OperationJob, bool> predicate, TimeSpan timeout, string description, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        OperationJob? last = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            last = await LoadJobAsync(jobId, ct);
            if (predicate(last)) return last;
            await Task.Delay(PollInterval, ct);
        }

        throw new TimeoutException(
            $"timed out after {timeout.TotalSeconds:0.#}s waiting for {description}; " +
            $"last observed: {Describe(last)}");
    }

    public Task<OperationJob> WaitForTerminalAsync(int jobId, TimeSpan timeout, CancellationToken ct) =>
        WaitAsync(jobId, j => Terminal.Contains(j.State), timeout, "the job to reach a terminal state", ct);

    public static bool IsTerminal(JobState state) => Terminal.Contains(state);

    public static string Describe(OperationJob? job) =>
        job is null
            ? "(no job)"
            : $"state={job.State} block={job.BlockReason} bytes={job.BytesProcessed}/{job.TotalBytes} " +
              $"items=[{string.Join(", ", job.Items.Select(i => i.State))}] error={job.ErrorMessage ?? "-"}";

    public async ValueTask DisposeAsync() => await StopWorkerAsync();
}
