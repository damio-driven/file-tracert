namespace FileTracert.Data.Cancellation;

/// <summary>
/// The "the process is going down" token, seen from the data layer.
///
/// <para>Step 14b — a read that is still stepping when the host starts stopping has to be told to
/// stop too, or the service outlives its <c>ShutdownTimeout</c> (§3). The request's own
/// <c>RequestAborted</c> is not enough for that case: Kestrel only aborts an in-flight request once
/// the shutdown budget has already been spent, which is exactly the deadline we are trying to
/// meet.</para>
///
/// <para>Data cannot reference the hosting abstractions, so the token arrives as this one-value
/// holder: <c>AddDataServices</c> registers <see cref="None"/> (compositions with no host — tests,
/// the hardware harness, the EF design-time factory), and the Host replaces it with one carrying
/// <c>IHostApplicationLifetime.ApplicationStopping</c>. Same TryAdd-then-Replace shape the realtime
/// publisher port already uses.</para>
///
/// <para>Immutable on purpose: a settable ambient token is a race waiting for a reader to observe
/// the old value.</para>
/// </summary>
public sealed class DatabaseShutdownSignal
{
    /// <summary>A signal that never fires — the default for a composition without a host.</summary>
    public static readonly DatabaseShutdownSignal None = new(CancellationToken.None);

    public DatabaseShutdownSignal(CancellationToken token) => Token = token;

    public CancellationToken Token { get; }
}
