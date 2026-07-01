using System.Collections.Concurrent;
using FileTracert.Contracts.Operations;

namespace FileTracert.Business.Operations;

/// <summary>
/// Thread-safe <see cref="IJobCancellationRegistry"/>. At most one job runs at a time in the
/// MVP (sequential FIFO processor), but the dictionary keeps this correct even if that changes.
/// </summary>
public sealed class JobCancellationRegistry : IJobCancellationRegistry
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _sources = new();

    public CancellationToken Register(int jobId, CancellationToken linkedTo)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);

        // Replace any stale source for the same job id (e.g. a re-run after a crash).
        if (_sources.TryRemove(jobId, out var old))
            old.Dispose();

        _sources[jobId] = cts;
        return cts.Token;
    }

    public void Cancel(int jobId)
    {
        if (_sources.TryGetValue(jobId, out var cts))
        {
            // The source may be disposed/removed concurrently as the job finishes — ignore.
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Remove(int jobId)
    {
        if (_sources.TryRemove(jobId, out var cts))
            cts.Dispose();
    }
}
