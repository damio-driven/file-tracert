using System.Diagnostics;
using System.Threading.Channels;
using FileTracert.Contracts.Logging;

namespace FileTracert.Host.Logging;

/// <summary>
/// Background pump between the <see cref="SqliteLogger"/> and the <see cref="ILogStore"/>:
/// a bounded channel drained by a single consumer that writes in batches. Enqueue is
/// non-blocking and never throws (overflow drops the newest record rather than
/// stalling the app); a sink failure never reaches the caller, so logging can never
/// crash the process. Disposing completes the channel and drains what is queued.
/// <para>
/// The sink cannot log its own failures through <c>ILogger</c> — that is the very
/// path that is broken — but silence is not the alternative (§9). Every loss is counted
/// (<see cref="DroppedRecordCount"/>, <see cref="FailedRecordCount"/>) and leaves a
/// breadcrumb <em>outside</em> the sink: stderr for a console run, <see cref="Trace"/> for
/// an attached debugger / DebugView when running as a service.
/// </para>
/// </summary>
public sealed class SqliteLogProcessor : IAsyncDisposable
{
    /// <summary>A failing sink fails for every batch: report it, do not narrate it.</summary>
    private static readonly TimeSpan BreadcrumbInterval = TimeSpan.FromSeconds(30);

    private readonly ILogStore _store;
    private readonly Channel<LogRecord> _channel;
    private readonly Task _consumer;
    private readonly int _batchSize;

    private long _droppedRecords;
    private long _failedRecords;
    private long _lastBreadcrumbTimestamp;

    public SqliteLogProcessor(ILogStore store, int capacity = 10_000, int batchSize = 200)
    {
        _store = store;
        _batchSize = batchSize;
        _channel = Channel.CreateBounded<LogRecord>(
            new BoundedChannelOptions(capacity)
            {
                // Logging must never block the application; under a flood we drop.
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            },
            // A DropWrite channel reports success even when it threw the record away: this
            // callback is the only place the loss is observable, so the counter hangs off it.
            itemDropped: OnRecordDropped);
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>Records the queue refused (flood, or a sink that already gave up).</summary>
    public long DroppedRecordCount => Interlocked.Read(ref _droppedRecords);

    /// <summary>Records the queue accepted but the store could not persist.</summary>
    public long FailedRecordCount => Interlocked.Read(ref _failedRecords);

    /// <summary>Queues a record for persistence. Non-blocking; never throws.</summary>
    public void Enqueue(LogRecord record)
    {
        // TryWrite says false only once the queue is closed — after the shutdown drain, or
        // because the consumer gave up. A record refused then is lost just the same as one
        // dropped by a full queue, so it goes through the same counter.
        if (!_channel.Writer.TryWrite(record))
        {
            OnRecordDropped(record);
        }
    }

    private void OnRecordDropped(LogRecord record)
    {
        var dropped = Interlocked.Increment(ref _droppedRecords);
        Breadcrumb(
            $"log queue dropped a record from '{record.Category}'; {dropped} record(s) dropped so far",
            exception: null);
    }

    private async Task ConsumeAsync()
    {
        var reader = _channel.Reader;
        var buffer = new List<LogRecord>(_batchSize);

        try
        {
            while (await reader.WaitToReadAsync())
            {
                buffer.Clear();
                while (buffer.Count < _batchSize && reader.TryRead(out var record))
                {
                    buffer.Add(record);
                }

                if (buffer.Count == 0)
                {
                    continue;
                }

                try
                {
                    await _store.WriteBatchAsync(buffer, CancellationToken.None);
                }
                catch (OperationCanceledException ex)
                {
                    // Expected noise on the way down (the store's connection going away),
                    // not a defect: traced, never shouted about (same rule as RealtimeEvents).
                    var failed = Interlocked.Add(ref _failedRecords, buffer.Count);
                    Trace.WriteLine(
                        $"[FileTracert] log batch of {buffer.Count} abandoned during shutdown " +
                        $"({failed} record(s) unwritten so far): {ex}");
                }
                catch (Exception ex)
                {
                    // A failure to persist logs must never take down the application — but it
                    // must not disappear either: the batch is counted and reported outside the sink.
                    var failed = Interlocked.Add(ref _failedRecords, buffer.Count);
                    Breadcrumb(
                        $"log sink failed to write a batch of {buffer.Count}; " +
                        $"{failed} record(s) unwritten so far",
                        ex);
                }
            }
        }
        catch (Exception ex)
        {
            // Defensive: the consumer loop itself must never surface an exception. Nothing
            // will be written from here on, so the queue is closed rather than left to
            // swallow records nobody reads — from now on every Enqueue counts as a drop.
            _channel.Writer.TryComplete();
            Breadcrumb("log queue consumer stopped; no further record will be persisted", ex, force: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a trace the sink itself cannot write. stderr is the visible channel in a console
    /// run and harmlessly discarded as a service; <see cref="Trace"/> reaches a debugger /
    /// DebugView in both. Throttled — a broken sink breaks for every batch — except where the
    /// message is one-off and structural.
    /// </summary>
    private void Breadcrumb(string message, Exception? exception, bool force = false)
    {
        if (!force && !ShouldWriteBreadcrumb())
        {
            return;
        }

        // Full detail (§9): ToString() carries the message, the stack and every inner exception.
        var line = exception is null
            ? $"[FileTracert] {message}"
            : $"[FileTracert] {message}: {exception}";

        Trace.WriteLine(line);
        try
        {
            Console.Error.WriteLine(line);
        }
        catch (IOException)
        {
            // No console (Windows Service) or a closed stderr: the Trace line above still stands.
        }
    }

    private bool ShouldWriteBreadcrumb()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastBreadcrumbTimestamp);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < BreadcrumbInterval)
        {
            return false;
        }

        // Whoever wins the exchange writes; a loser in the same instant stays quiet.
        return Interlocked.CompareExchange(ref _lastBreadcrumbTimestamp, now, last) == last;
    }
}
