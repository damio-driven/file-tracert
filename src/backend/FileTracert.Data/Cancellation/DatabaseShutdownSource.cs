namespace FileTracert.Data.Cancellation;

/// <summary>
/// Owns the token behind <see cref="DatabaseShutdownSignal"/> and decides WHEN it fires.
///
/// <para><b>Not <c>ApplicationStopping</c>, and the reason is a real failure mode.</b> That event is
/// raised at the very beginning of the stop sequence, while every <c>BackgroundService</c> is still
/// running and its own <c>stoppingToken</c> is still live — the host cancels those one at a time,
/// afterwards. A read interrupted in that window throws an <see cref="OperationCanceledException"/>
/// that the workers' <c>when (ct.IsCancellationRequested)</c> filters do not match, so a clean stop
/// during a scan would be logged as <em>«Scansione fallita»</em> and raise a user-facing
/// Notification. §9 asks for an interruption to be distinguished from an error, not dressed up as
/// one.</para>
///
/// <para>So the Host fires this from a hosted service registered immediately after the log flush,
/// i.e. third: hosted services stop in reverse registration order, so it runs after every worker has
/// already been stopped and before <c>GenericWebHostService</c> — which is registered first and
/// therefore drains Kestrel last. The signal lands exactly in the gap where the only reads left are
/// the in-flight HTTP requests, which is the wait step 13 measured at over 270 s.</para>
/// </summary>
public sealed class DatabaseShutdownSource : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public DatabaseShutdownSource() => Signal = new DatabaseShutdownSignal(_cts.Token);

    /// <summary>The read-side view of this source; what everything else takes as a dependency.</summary>
    public DatabaseShutdownSignal Signal { get; }

    /// <summary>Tells every guarded read still running that the process is going down.</summary>
    public void Stop()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }

    public void Dispose() => _cts.Dispose();
}
