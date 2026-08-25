using System.Data.Common;
using FileTracert.Data.Cancellation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FileTracert.Data.Interceptors;

/// <summary>
/// Step 14b — puts every EF READ under <see cref="SqliteReadGuard"/>, so a cancelled query stops
/// stepping instead of running to completion for nobody.
///
/// <para><b>Reads only, and said so by EF rather than inferred.</b> The perimeter is an ALLOW-LIST
/// on <see cref="CommandSource"/>: a LINQ query or a <c>FromSql</c> query, nothing else. Inferring
/// it from the execution path would be wrong, and the review of this step caught it: EF sends a
/// <c>SaveChanges</c> batch through <c>ExecuteReaderAsync</c> too — it has to read back
/// <c>changes()</c> / <c>RETURNING</c> — and under the default
/// <c>AutoTransactionBehavior.WhenNeeded</c> a batch of ONE command opens no transaction at all.
/// So "reader path" and "has no DbTransaction" are both true of the queue's own checkpoint writes
/// (<c>JobExecutionEngine</c> saves a single item row and passes a cancellable token), and
/// interrupting one of those is exactly what the task forbids. The allow-list cannot be fooled that
/// way: <c>SaveChanges</c>, <c>Migrations</c>, <c>BulkUpdate</c>, <c>ValueGenerator</c> and
/// <c>Unknown</c> are all outside it.</para>
///
/// <para><b>And still not inside a transaction.</b> A read that carries a <c>DbTransaction</c>
/// belongs to a write unit of work (the state machine reads a job before it moves it). Aborting it
/// is not free the way an isolated SELECT is, so it is left alone — a second, independent way the
/// queue's connections stay outside this mechanism.</para>
///
/// <para><b>Async only.</b> The synchronous overloads take no <see cref="CancellationToken"/>:
/// there is no cancellation to bridge, so there is nothing to do and nothing to pay.</para>
///
/// <para><b>Why it executes the command itself.</b> Suppressing EF's own call is the documented way
/// to replace an execution, and it is the only place that can see both the interrupt and the token
/// that caused it — which is what lets the failure be re-thrown as an
/// <see cref="OperationCanceledException"/> instead of reaching the log as a database error.</para>
/// </summary>
public sealed class SqliteReadCancellationInterceptor : DbCommandInterceptor
{
    private readonly DatabaseShutdownSignal _shutdown;
    private readonly ILogger<SqliteReadCancellationInterceptor> _logger;

    public SqliteReadCancellationInterceptor(
        DatabaseShutdownSignal shutdown, ILogger<SqliteReadCancellationInterceptor> logger)
    {
        _shutdown = shutdown;
        _logger = logger;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (result.HasResult || !ShouldGuard(command, eventData, cancellationToken))
        {
            return result;
        }

        var reader = await SqliteReadGuard.ExecuteAsync(
            command, cancellationToken, _shutdown.Token, _logger,
            ct => command.ExecuteReaderAsync(ct)).ConfigureAwait(false);

        return InterceptionResult<DbDataReader>.SuppressWithResult(reader);
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        if (result.HasResult || !ShouldGuard(command, eventData, cancellationToken))
        {
            return result;
        }

        var value = await SqliteReadGuard.ExecuteAsync(
            command, cancellationToken, _shutdown.Token, _logger,
            ct => command.ExecuteScalarAsync(ct)).ConfigureAwait(false);

        return InterceptionResult<object>.SuppressWithResult(value!);
    }

    private static bool ShouldGuard(
        DbCommand command, CommandEventData eventData, CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
           && command.Transaction is null
           && command.Connection is SqliteConnection
           && eventData.CommandSource is CommandSource.LinqQuery or CommandSource.FromSqlQuery;
}
