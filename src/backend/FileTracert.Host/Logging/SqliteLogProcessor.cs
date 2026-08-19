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

    /// <summary>Upper bound accepted from configuration: past this a "drain" is a hang.</summary>
    public static readonly TimeSpan MaxDrainTimeout = TimeSpan.FromMinutes(1);

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
    private long _lastDropBreadcrumb;
    private long _lastFailureBreadcrumb;
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
        // A drain budget is configuration, and configuration can be wrong: a negative TimeSpan
        // would make Task.Delay throw straight out of StopAsync — a failed shutdown caused by
        // the very code that exists to make shutdowns clean. Out-of-range values fall back to
        // the default rather than being trusted.
        _drainTimeout = drainTimeout is { } requested && requested > TimeSpan.Zero && requested <= MaxDrainTimeout
            ? requested
            : DefaultDrainTimeout;
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

                // Published as soon as the batch exists — before the write, not inside it — so a
                // drain that gives up can report the records it walked away from. Only the batch
                // still being assembled above can escape the count.
                Volatile.Write(ref _inFlightRecords, buffer.Count);

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
    /// <para>
    /// Runs once: a second call returns immediately rather than waiting for the first to finish.
    /// Every call site is the stop sequence, where those calls are sequential; the guarantee is
    /// "the queue is closed and its drain has been started", not "the drain has completed".
    /// </para>
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
            await ReportLossesAsync().ConfigureAwait(false);
            return;
        }

        // Still queued, plus the batch the consumer was in the middle of writing (short by at
        // most the batch being assembled). Reported as ABANDONED, not failed: the consumer is
        // still running and may yet write them (or fail and count them itself) — nobody will be
        // here to find out, and a counter that guessed would either double-count or claim a loss
        // that did not happen.
        var abandoned = _channel.Reader.Count + Volatile.Read(ref _inFlightRecords);
        Interlocked.Exchange(ref _abandonedRecords, abandoned);
        Breadcrumb(
            $"log queue drain gave up after {_drainTimeout.TotalSeconds:0.#}s; " +
            $"{abandoned} record(s) abandoned (queued or mid-write)",
            exception: null,
            force: true);

        // No summary row here on purpose: the store is the thing that did not answer in time, so
        // writing to it now would spend the budget the cap just enforced. The breadcrumb stands.
        ReportLosses(persist: false);
    }

    /// <summary>
    /// Last word of the sink. The breadcrumb goes outside the logging pipeline — by the time the
    /// queue is closed an <c>ILogger</c> call would be dropped by this sink and can throw in
    /// another (a provider disposed earlier in the stop sequence). Returns the summary, or null
    /// when there is nothing to report.
    /// </summary>
    private string? ReportLosses(bool persist)
    {
        var dropped = DroppedRecordCount;
        var failed = FailedRecordCount;
        var abandoned = AbandonedRecordCount;
        if (dropped == 0 && failed == 0 && abandoned == 0)
        {
            return null;
        }

        var summary =
            $"log sink ended the run with {dropped} dropped, {failed} unwritten and " +
            $"{abandoned} abandoned record(s)";
        Breadcrumb(summary, exception: null, force: true);
        return persist ? summary : null;
    }

    /// <summary>
    /// The same summary, but written <b>into the log database</b> — straight through the store,
    /// bypassing the closed queue (so there is no recursion). stderr is discarded when running as
    /// a Windows Service and <see cref="Trace"/> needs a debugger attached: without this row the
    /// §9 trace would exist only where the production deployment cannot see it.
    /// </summary>
    private async Task ReportLossesAsync()
    {
        var summary = ReportLosses(persist: true);
        if (summary is null)
        {
            return;
        }

        try
        {
            await _store.WriteBatchAsync(
                [
                    new LogRecord(
                        DateTime.UtcNow,
                        (int)LogLevel.Warning,
                        typeof(SqliteLogProcessor).FullName!,
                        summary,
                        Exception: null,
                        EventId: null,
                        Scope: null)
                ],
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The store just finished writing a whole queue, so this is unexpected — and it is
            // the last thing the sink does, so it can only be said out here.
            Breadcrumb("could not record the loss summary in the log database", ex, force: true);
        }
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
        // Two throttle slots, not one: a flood of dropped records must not be able to eat the
        // slot of a genuine sink failure — the two say very different things.
        if (!force)
        {
            var allowed = exception is null
                ? ShouldWriteBreadcrumb(ref _lastDropBreadcrumb)
                : ShouldWriteBreadcrumb(ref _lastFailureBreadcrumb);
            if (!allowed)
            {
                return;
            }
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

    private static bool ShouldWriteBreadcrumb(ref long slot)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref slot);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < BreadcrumbInterval)
        {
            return false;
        }

        // Whoever wins the exchange writes; a loser in the same instant stays quiet.
        return Interlocked.CompareExchange(ref slot, now, last) == last;
    }
}
