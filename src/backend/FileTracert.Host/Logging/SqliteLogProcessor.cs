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
/// (<see cref="DroppedRecordCount"/>, <see cref="FailedRecordCount"/>,
/// <see cref="AbandonedRecordCount"/>) and leaves a
/// breadcrumb <em>outside</em> the sink: stderr for a console run, <see cref="Trace"/> for
/// an attached debugger / DebugView when running as a service.
/// </para>
/// </summary>
public sealed class SqliteLogProcessor : IAsyncDisposable
{
    /// <summary>Default budget for the shutdown drain; well inside the host's own timeout.</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A failing sink fails for every batch: report it, do not narrate it.</summary>
    private static readonly TimeSpan BreadcrumbInterval = TimeSpan.FromSeconds(30);

    private readonly ILogStore _store;
    private readonly Channel<LogRecord> _channel;
    private readonly Task _consumer;
    private readonly int _batchSize;
    private readonly TimeSpan _drainTimeout;

    private long _droppedRecords;
    private long _failedRecords;
    private long _abandonedRecords;
    private long _lastBreadcrumbTimestamp;
    private int _inFlightRecords;
    private int _draining;

    public SqliteLogProcessor(
        ILogStore store,
        int capacity = 10_000,
        int batchSize = 200,
        TimeSpan? drainTimeout = null)
    {
        _store = store;
        _batchSize = batchSize;
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
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

    /// <summary>
    /// Records still queued (or mid-write) when the shutdown drain ran out of budget. Kept apart
    /// from <see cref="FailedRecordCount"/> on purpose: the abandoned consumer keeps running, so
    /// these may still land or may still fail, and folding them into "unwritten" would either
    /// count them twice or claim a loss that did not happen.
    /// </summary>
    public long AbandonedRecordCount => Interlocked.Read(ref _abandonedRecords);

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
                    // Published so a drain that gives up can report the batch it abandoned
                    // mid-write, not just what was still queued behind it.
                    Volatile.Write(ref _inFlightRecords, buffer.Count);
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
                finally
                {
                    Volatile.Write(ref _inFlightRecords, 0);
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

    /// <summary>
    /// Closes the queue and waits — <b>bounded</b> — for the consumer to write what is left.
    /// The cap matters as much as the drain: a store that hangs would otherwise turn a service
    /// that stops into a service that never stops. On timeout (or when the caller's own
    /// shutdown budget runs out) the consumer is abandoned — the process is going away anyway —
    /// and the records left behind are counted and reported.
    /// </summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _draining, 1) == 1)
        {
            return;
        }

        _channel.Writer.TryComplete();

        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = Task.Delay(_drainTimeout, cap.Token);
        var finished = await Task.WhenAny(_consumer, timeout).ConfigureAwait(false);
        await cap.CancelAsync().ConfigureAwait(false);

        if (finished == _consumer)
        {
            // The consumer loop never lets an exception out, so this only observes completion.
            await _consumer.ConfigureAwait(false);
            ReportLosses();
            return;
        }

        // Still queued, plus the batch the consumer was in the middle of writing. Reported as
        // ABANDONED, not failed: the consumer is still running and may yet write them (or fail
        // and count them itself) — nobody will be here to find out, and a counter that guessed
        // would either double-count or claim a loss that did not happen.
        var abandoned = _channel.Reader.Count + Volatile.Read(ref _inFlightRecords);
        Interlocked.Exchange(ref _abandonedRecords, abandoned);
        Breadcrumb(
            $"log queue drain gave up after {_drainTimeout.TotalSeconds:0.#}s; " +
            $"{abandoned} record(s) abandoned (queued or mid-write)",
            exception: null,
            force: true);
        ReportLosses();
    }

    /// <summary>
    /// Last word of the sink, written outside the logging pipeline — by the time the queue is
    /// closed, an <c>ILogger</c> call would be dropped by this sink and can throw in another
    /// (a provider disposed earlier in the stop sequence).
    /// </summary>
    private void ReportLosses()
    {
        var dropped = DroppedRecordCount;
        var failed = FailedRecordCount;
        var abandoned = AbandonedRecordCount;
        if (dropped == 0 && failed == 0 && abandoned == 0)
        {
            return;
        }

        Breadcrumb(
            $"log sink ended the run with {dropped} dropped, {failed} unwritten and " +
            $"{abandoned} abandoned record(s)",
            exception: null,
            force: true);
    }

    public ValueTask DisposeAsync() => new(DrainAsync());

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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // No console (Windows Service) or stderr already closed at shutdown. Nothing is lost
            // in silence: the same line went to Trace above — and a breadcrumb must never become
            // the reason a stop fails.
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
