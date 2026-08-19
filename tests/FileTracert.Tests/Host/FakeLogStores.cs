using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Logging;
using FileTracert.Contracts.Paging;

namespace FileTracert.Tests.Host;

/// <summary>Base for log-store fakes: only <c>WriteBatchAsync</c> is ever exercised.</summary>
internal abstract class LogStoreFake : ILogStore
{
    public void EnsureSchema() { }

    public abstract Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct);

    public Task<PagedResult<LogEntryDto>> QueryAsync(LogQuery query, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<int> TrimAsync(DateTime olderThanUtc, int maxRows, bool vacuum, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task CheckpointAsync(CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A sink that always fails, to exercise the processor's failure path.</summary>
internal sealed class ThrowingLogStore : LogStoreFake
{
    public override Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct) =>
        throw new InvalidOperationException("disk full");
}

/// <summary>A sink that stalls on the first batch until the test releases it.</summary>
internal sealed class BlockingLogStore : LogStoreFake
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Entered { get; private set; }

    public override async Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct)
    {
        Entered = true;
        await _gate.Task;
    }

    public void Release() => _gate.TrySetResult();
}

/// <summary>
/// A sink slow enough that "the queue was drained before the host finished stopping" cannot
/// be confused with "the background consumer happened to catch up in the meantime".
/// </summary>
internal sealed class SlowRecordingLogStore(TimeSpan delayPerBatch) : LogStoreFake
{
    private readonly Lock _sync = new();
    private readonly List<LogRecord> _written = [];

    public IReadOnlyList<LogRecord> Written
    {
        get
        {
            lock (_sync)
            {
                return _written.ToList();
            }
        }
    }

    public override async Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct)
    {
        await Task.Delay(delayPerBatch, ct);
        lock (_sync)
        {
            _written.AddRange(records);
        }
    }
}

/// <summary>A sink that never returns: the drain must give up on its own, on a cap.</summary>
internal sealed class HangingLogStore : LogStoreFake
{
    public override Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct) =>
        new TaskCompletionSource().Task;
}
