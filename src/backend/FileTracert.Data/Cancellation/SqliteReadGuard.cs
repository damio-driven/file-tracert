using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace FileTracert.Data.Cancellation;

/// <summary>
/// Makes a SQLite READ obey a <see cref="CancellationToken"/> while it is running, not merely
/// between two awaits.
///
/// <para><b>Why this type exists.</b> <c>Microsoft.Data.Sqlite</c> implements
/// <c>DbCommand.Cancel()</c> as a no-op and never looks at the token once
/// <c>sqlite3_step</c> is under way. So a cancelled request stopped being awaited but kept
/// burning a core: the step 13 soak measured +147 s of CPU in 119 s of wall clock after the
/// client had already given up, and an <c>sc stop</c> that stayed <c>StopPending</c> for over
/// 270 s against the 30 s <c>ShutdownTimeout</c> promised by §3. A performance defect turned
/// into a shutdown defect.</para>
///
/// <para><b>How.</b> <c>sqlite3_interrupt</c> is the one API that reaches inside a running
/// statement; it makes the current step fail with <c>SQLITE_INTERRUPT</c>. It is called from the
/// token's callback, and the registration lives exactly as long as the ADO.NET call: SQLite
/// documents an interrupt raised while no statement is running as a no-op, and
/// <see cref="CancellationTokenRegistration.Dispose"/> blocks until an in-flight callback has
/// returned — so by the time this method returns, no interrupt can still be in flight against a
/// connection that is about to go back to the pool. Nothing here touches pool lifetime; the
/// process-wide accident of step 11i is not reachable from this code.</para>
///
/// <para><b>Perimeter — reads only, and only where the caller already agreed to be cancelled.</b>
/// Interrupting a SELECT throws away nothing; interrupting a write inside an explicit transaction
/// makes SQLite roll the whole transaction back, which is precisely the crash-safety discipline
/// the queue owns. So writes never come through here (see
/// <c>SqliteReadCancellationInterceptor</c>), and a command executed with a token that
/// <see cref="CancellationToken.CanBeCanceled"/> is false is left alone: passing
/// <see cref="CancellationToken.None"/> is a deliberate "this must run to completion" all over the
/// queue engine, and it stays deliberate.</para>
///
/// <para><b>The exception is translated.</b> An interrupt we asked for is a cancellation, not a
/// database failure: it surfaces as <see cref="OperationCanceledException"/> so callers, ASP.NET
/// Core and EF's own logging all read it as what it is (§9 — logged, and distinct from an error).
/// The translation is conditional on this guard having actually fired, so a
/// <c>SQLITE_INTERRUPT</c> raised by anything else still travels as the error it is.</para>
/// </summary>
public static class SqliteReadGuard
{
    /// <summary>
    /// Runs <paramref name="execute"/> with <paramref name="command"/>'s connection interruptible
    /// by either token.
    /// </summary>
    /// <param name="command">The command about to run. Its connection must be open.</param>
    /// <param name="cancellationToken">The caller's token — a request being aborted, a worker
    /// being stopped. Not cancellable ⇒ no guard is installed and nothing is paid.</param>
    /// <param name="shutdownToken">The process-is-stopping token
    /// (<see cref="DatabaseShutdownSignal"/>), or <c>default</c> for callers that have none.</param>
    /// <param name="logger">Where the interruption is reported. Optional: the log store cannot log
    /// through itself.</param>
    /// <param name="execute">The ADO.NET call to make.</param>
    public static async Task<T> ExecuteAsync<T>(
        DbCommand command,
        CancellationToken cancellationToken,
        CancellationToken shutdownToken,
        ILogger? logger,
        Func<CancellationToken, Task<T>> execute)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var handle = HandleOf(command);
        if (handle is null || !cancellationToken.CanBeCanceled)
        {
            return await execute(cancellationToken).ConfigureAwait(false);
        }

        // Already stopping: do not START a long read now. An interrupt raised while no statement is
        // running is documented as a no-op — it does not arm anything for the next one — so a
        // registration on an already-cancelled token would fire against an idle connection and the
        // statement would then run to completion regardless. Refusing here is both the deterministic
        // answer and the honest one; a read that deliberately passed a token which cannot be
        // cancelled never reaches this line and is left to finish.
        if (shutdownToken.IsCancellationRequested)
        {
            throw Refused(command, shutdownToken, logger);
        }

        var interrupter = new Interrupter(handle, logger);

        // Disposal order is the reverse of declaration: both registrations are gone — and any
        // callback already running has returned — before this method hands the connection back.
        using var onCaller = cancellationToken.Register(interrupter.Fire);
        using var onShutdown = shutdownToken.CanBeCanceled
            ? shutdownToken.Register(interrupter.Fire)
            : default;

        // Re-asked AFTER both registrations: a shutdown that landed in between would have run the
        // callback against an idle connection -- a no-op by SQLite's own contract -- and armed
        // nothing, so the statement below would have run to the end with no way to stop it. The
        // caller's token is re-asked with it for the same reason.
        if (shutdownToken.IsCancellationRequested)
        {
            throw Refused(command, shutdownToken, logger);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await execute(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (interrupter.Fired && ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            logger?.LogInformation(
                "SQLite read interrupted after cancellation ({Reason}). {Sql}",
                cancellationToken.IsCancellationRequested ? "request cancelled" : "host stopping",
                command.CommandText);

            throw new OperationCanceledException(
                "The read was interrupted because it was cancelled.", ex, cancellationToken);
        }
    }

    /// <summary>
    /// A read the host will not let start. Logged, because during a shutdown this is what every
    /// guarded read hits and "the host refused N reads while stopping" must not be invisible (§9).
    /// </summary>
    private static OperationCanceledException Refused(
        DbCommand command, CancellationToken shutdownToken, ILogger? logger)
    {
        logger?.LogInformation(
            "SQLite read not started: the host is shutting down. {Sql}", command.CommandText);

        return new OperationCanceledException(
            "The read was not started: the host is shutting down.", shutdownToken);
    }

    /// <summary>
    /// The native handle behind an open <see cref="SqliteConnection"/>, or <c>null</c> when there
    /// is nothing to interrupt (another provider, or a connection that is not open — in which case
    /// no statement is running and the guard has no work).
    /// </summary>
    private static sqlite3? HandleOf(DbCommand command)
        => command.Connection is SqliteConnection { State: System.Data.ConnectionState.Open } sqlite
            ? sqlite.Handle
            : null;

    /// <summary>
    /// One interrupt per guarded command, at most. A second token firing has nothing to add, and
    /// the callback must never throw: it runs on whatever thread cancelled (Kestrel's, the host's
    /// stop sequence), where an exception would surface far from here as an
    /// <see cref="AggregateException"/> out of <c>CancellationTokenSource.Cancel</c>.
    /// </summary>
    private sealed class Interrupter(sqlite3 handle, ILogger? logger)
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) != 0;

        public void Fire()
        {
            if (Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }

            try
            {
                raw.sqlite3_interrupt(handle);
            }
            catch (Exception ex)
            {
                // Resilience yes, silence no (§9). Losing the interrupt costs us the CPU this
                // guard exists to reclaim; losing the stop sequence would cost more.
                logger?.LogWarning(ex, "Failed to interrupt the running SQLite statement.");
            }
        }
    }
}
