using Microsoft.Data.Sqlite;

namespace FileTracert.Tests.Data;

/// <summary>
/// Counts the statements issued straight on the connection.
///
/// <para><see cref="CountingCommandInterceptor"/> only sees what EF sends, and the indexing hot
/// path deliberately does not go through EF: <c>BulkIndexWriter</c> builds its own
/// <see cref="SqliteCommand"/>s so the merge and the scan closure stay set-based SQL. This is the
/// same unit of measure — statements, not milliseconds — for that half of the code.</para>
///
/// <para>One command per statement the writer issues, with one exception it makes on purpose: the
/// staging fill prepares a single command and re-executes it for every staged row. So a test that
/// wants to prove a pass has not become per-ROW asserts that the count does not move when the
/// number of rows behind the same input grows.</para>
/// </summary>
internal sealed class CountingSqliteConnection(string connectionString) : SqliteConnection(connectionString)
{
    private int _statements;

    public int Statements => Volatile.Read(ref _statements);

    public void Reset() => Volatile.Write(ref _statements, 0);

    public override SqliteCommand CreateCommand()
    {
        Interlocked.Increment(ref _statements);
        return base.CreateCommand();
    }
}
