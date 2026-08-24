using Microsoft.Data.Sqlite;

namespace FileTracert.Tests.Data;

/// <summary>
/// Records the SQL — and the parameter values — of every statement executed straight on the
/// connection, so a test can ask SQLite how it PLANS the statement the product really issues.
///
/// <para>It exists because the alternative is an echo: a test that pastes its own copy of the
/// search SQL and explains that proves the copy is planned well, not the product. The search
/// builds its commands by hand (<c>SqliteCommand</c>, not EF), so no EF interceptor sees them —
/// the same reason <see cref="CountingSqliteConnection"/> exists, one level deeper: that one
/// counts statements, this one keeps them.</para>
/// </summary>
internal sealed class CapturingSqliteConnection(string connectionString) : SqliteConnection(connectionString)
{
    private readonly List<CapturedStatement> _statements = [];

    public IReadOnlyList<CapturedStatement> Statements
    {
        get { lock (_statements) return [.. _statements]; }
    }

    public void Reset()
    {
        lock (_statements) _statements.Clear();
    }

    private void Record(SqliteCommand cmd)
    {
        var captured = new CapturedStatement(
            cmd.CommandText,
            [.. cmd.Parameters.Cast<SqliteParameter>().Select(p => (p.ParameterName, p.Value))]);

        lock (_statements) _statements.Add(captured);
    }

    public override SqliteCommand CreateCommand()
    {
        // The base does more than `new SqliteCommand { Connection = this }`: it also attaches the
        // connection's AMBIENT transaction, and a command without it throws the moment anything
        // (EF's migrations, for one) runs inside a transaction. So the base builds a correctly
        // wired command and this one copies the wiring rather than guessing at it.
        using var wired = base.CreateCommand();
        return new RecordingCommand(this) { Connection = this, Transaction = wired.Transaction };
    }

    public sealed record CapturedStatement(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters);

    /// <summary>
    /// Records at EXECUTION time, not at creation: only then are the text and the parameters the
    /// ones the statement actually ran with.
    /// </summary>
    private sealed class RecordingCommand(CapturingSqliteConnection owner) : SqliteCommand
    {
        // One override, because there is one funnel: SqliteCommand declares its own
        // `new virtual SqliteDataReader ExecuteReader(CommandBehavior)`, and ExecuteScalar,
        // ExecuteNonQuery and every async overload all end up calling it. Overriding the
        // ADO.NET-shaped members instead captures some statements twice and others not at all.
        public override SqliteDataReader ExecuteReader(System.Data.CommandBehavior behavior)
        {
            owner.Record(this);
            return base.ExecuteReader(behavior);
        }
    }
}
