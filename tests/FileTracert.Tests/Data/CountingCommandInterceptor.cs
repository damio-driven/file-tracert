using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FileTracert.Tests.Data;

/// <summary>
/// Counts the SQL statements EF actually sends, and keeps their text.
///
/// This is the unit the efficiency work is measured in: on SQLite — one writer, one process —
/// "how many statements" and "how many passes over the big table" are facts, while milliseconds in
/// a test are the machine's mood. A test that asserts a statement count fails the moment someone
/// puts a query back into a loop, on any hardware.
/// </summary>
public sealed class CountingCommandInterceptor : DbCommandInterceptor, IDbTransactionInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();
    private int _transactions;

    public IReadOnlyCollection<string> Commands => [.. _commands];

    public int Count => _commands.Count;

    /// <summary>
    /// Explicit transactions opened since the last <see cref="Reset"/>. On SQLite this is the
    /// count that matters: one writer, so every transaction is a turn at the only write lock in
    /// the process.
    /// </summary>
    public int Transactions => Volatile.Read(ref _transactions);

    public DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Interlocked.Increment(ref _transactions);
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _transactions);
        return ValueTask.FromResult(result);
    }

    /// <summary>Statements whose text contains <paramref name="fragment"/> (case-insensitive).</summary>
    public int CountContaining(string fragment) =>
        _commands.Count(c => c.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public void Reset()
    {
        _commands.Clear();
        Volatile.Write(ref _transactions, 0);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        _commands.Enqueue(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        _commands.Enqueue(command.CommandText);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        _commands.Enqueue(command.CommandText);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
