using System.Globalization;
using System.Text;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Logging;
using FileTracert.Contracts.Paging;
using Microsoft.Data.Sqlite;

namespace FileTracert.Data.Logging;

/// <summary>
/// <see cref="ILogStore"/> over a dedicated SQLite database, using raw
/// <see cref="SqliteConnection"/> (never the main <c>DbContext</c>) so logging is
/// independent of the data layer's lifecycle and write contention. Timestamps are
/// stored as round-trippable ISO-8601 UTC text, so lexical ordering equals
/// chronological ordering and range filters are plain text comparisons.
/// </summary>
public sealed class SqliteLogStore : ILogStore
{
    private const string TimeFormat = "o";
    private readonly string _connectionString;

    public SqliteLogStore(string connectionString) => _connectionString = connectionString;

    public void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS LogEntries (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT    NOT NULL,
                Level        INTEGER NOT NULL,
                Category     TEXT    NOT NULL,
                Message      TEXT    NOT NULL,
                Exception    TEXT        NULL,
                EventId      INTEGER     NULL,
                Scope        TEXT        NULL
            );
            CREATE INDEX IF NOT EXISTS IX_LogEntries_TimestampUtc ON LogEntries(TimestampUtc);
            CREATE INDEX IF NOT EXISTS IX_LogEntries_Level ON LogEntries(Level);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using var conn = Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO LogEntries (TimestampUtc, Level, Category, Message, Exception, EventId, Scope)
            VALUES ($ts, $level, $cat, $msg, $ex, $eid, $scope);
            """;
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
        var pLevel = cmd.Parameters.Add("$level", SqliteType.Integer);
        var pCat = cmd.Parameters.Add("$cat", SqliteType.Text);
        var pMsg = cmd.Parameters.Add("$msg", SqliteType.Text);
        var pEx = cmd.Parameters.Add("$ex", SqliteType.Text);
        var pEid = cmd.Parameters.Add("$eid", SqliteType.Integer);
        var pScope = cmd.Parameters.Add("$scope", SqliteType.Text);

        foreach (var r in records)
        {
            pTs.Value = r.TimestampUtc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
            pLevel.Value = r.Level;
            pCat.Value = r.Category;
            pMsg.Value = r.Message;
            pEx.Value = (object?)r.Exception ?? DBNull.Value;
            pEid.Value = (object?)r.EventId ?? DBNull.Value;
            pScope.Value = (object?)r.Scope ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<PagedResult<LogEntryDto>> QueryAsync(LogQuery query, CancellationToken ct)
    {
        var (where, parameters) = BuildFilter(query);

        await using var conn = Open();

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM LogEntries{where};";
        AddParameters(countCmd, parameters);
        var total = Convert.ToInt32((long)(await countCmd.ExecuteScalarAsync(ct))!);

        await using var pageCmd = conn.CreateCommand();
        pageCmd.CommandText =
            $"""
             SELECT Id, TimestampUtc, Level, Category, Message, Exception, EventId, Scope
             FROM LogEntries{where}
             ORDER BY TimestampUtc DESC, Id DESC
             LIMIT $take OFFSET $skip;
             """;
        AddParameters(pageCmd, parameters);
        pageCmd.Parameters.AddWithValue("$take", query.Take <= 0 ? 50 : query.Take);
        pageCmd.Parameters.AddWithValue("$skip", query.Skip < 0 ? 0 : query.Skip);

        var items = new List<LogEntryDto>();
        await using var reader = await pageCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new LogEntryDto(
                Id: reader.GetInt64(0),
                TimestampUtc: DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Level: LogLevelNames.ToName(reader.GetInt32(2)),
                Category: reader.GetString(3),
                Message: reader.GetString(4),
                Exception: reader.IsDBNull(5) ? null : reader.GetString(5),
                EventId: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Scope: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return new PagedResult<LogEntryDto>(items, total, query.Skip, query.Take);
    }

    public async Task<int> TrimAsync(DateTime olderThanUtc, int maxRows, bool vacuum, CancellationToken ct)
    {
        await using var conn = Open();
        var removed = 0;

        await using (var byAge = conn.CreateCommand())
        {
            byAge.CommandText = "DELETE FROM LogEntries WHERE TimestampUtc < $cutoff;";
            byAge.Parameters.AddWithValue(
                "$cutoff",
                olderThanUtc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture));
            removed += await byAge.ExecuteNonQueryAsync(ct);
        }

        if (maxRows > 0)
        {
            await using var byCap = conn.CreateCommand();
            byCap.CommandText =
                """
                DELETE FROM LogEntries
                WHERE Id NOT IN (SELECT Id FROM LogEntries ORDER BY Id DESC LIMIT $max);
                """;
            byCap.Parameters.AddWithValue("$max", maxRows);
            removed += await byCap.ExecuteNonQueryAsync(ct);
        }

        if (vacuum && removed > 0)
        {
            await using var vac = conn.CreateCommand();
            vac.CommandText = "VACUUM;";
            await vac.ExecuteNonQueryAsync(ct);
        }

        return removed;
    }

    private static (string Where, List<(string Name, object Value)> Parameters) BuildFilter(LogQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<(string, object)>();

        if (query.MinLevel is { } minLevel)
        {
            clauses.Add("Level >= $minLevel");
            parameters.Add(("$minLevel", minLevel));
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            // Prefix match (StartsWith): "FileTracert" matches "FileTracert.Host" etc.
            // Escape LIKE wildcards so the value is treated literally.
            clauses.Add(@"Category LIKE $category ESCAPE '\'");
            parameters.Add(("$category", EscapeLike(query.Category) + "%"));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            clauses.Add("(Message LIKE $search OR Exception LIKE $search)");
            parameters.Add(("$search", $"%{query.Search}%"));
        }

        if (query.FromUtc is { } from)
        {
            clauses.Add("TimestampUtc >= $from");
            parameters.Add(("$from", from.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture)));
        }

        if (query.ToUtc is { } to)
        {
            clauses.Add("TimestampUtc <= $to");
            parameters.Add(("$to", to.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture)));
        }

        if (clauses.Count == 0)
        {
            return (string.Empty, parameters);
        }

        var sb = new StringBuilder(" WHERE ");
        sb.AppendJoin(" AND ", clauses);
        return (sb.ToString(), parameters);
    }

    /// <summary>Escapes LIKE wildcards (% _ and the escape char) so a value matches literally.</summary>
    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static void AddParameters(SqliteCommand cmd, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
