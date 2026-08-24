using System.Globalization;
using System.Text;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Logging;
using FileTracert.Contracts.Paging;
using FileTracert.Data.Cancellation;
using FileTracert.Data.Interceptors;
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

    /// <summary>
    /// The one READ this store serves. 14b — both statements go through
    /// <see cref="SqliteReadGuard"/>: a <c>LIKE '%…%'</c> over a log database that step 13 found
    /// holding 500 000 rows is a full scan, i.e. exactly the shape that used to keep burning a core
    /// after the screen that asked for it had gone away. No shutdown signal is threaded here: this
    /// store is built before the container exists (it has to be — it is the sink everything logs
    /// through, including startup), so it has none to link, and its only caller is an HTTP request
    /// whose own token covers the observed failure. No logger either, for the obvious reason.
    /// </summary>
    public async Task<PagedResult<LogEntryDto>> QueryAsync(LogQuery query, CancellationToken ct)
    {
        var (where, parameters) = BuildFilter(query);

        await using var conn = Open();

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM LogEntries{where};";
        AddParameters(countCmd, parameters);
        var total = Convert.ToInt32((long)(await SqliteReadGuard.ExecuteAsync(
            countCmd, ct, default, null, c => countCmd.ExecuteScalarAsync(c)))!);

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
        await using var reader = await SqliteReadGuard.ExecuteAsync(
            pageCmd, ct, default, null, c => pageCmd.ExecuteReaderAsync(c));
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
            // Contains match, on the same terms as the Category filter above: what the user
            // typed is a literal, so '%' and '_' are escaped — searching for "100%" or
            // "file_name" must not turn into a wildcard that matches anything.
            clauses.Add(@"(Message LIKE $search ESCAPE '\' OR Exception LIKE $search ESCAPE '\')");
            parameters.Add(("$search", $"%{EscapeLike(query.Search)}%"));
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

    /// <summary>
    /// Merges the WAL back into the main log file and truncates it. The log DB manages its
    /// own connection (never the main <c>DbContext</c>), so it is not covered by the EF
    /// checkpoint at startup nor by <c>SqliteBusyTimeoutInterceptor</c>; without this its
    /// WAL grows without bound under constant logging (observed: 185 MB). Best-effort — if a
    /// concurrent writer holds the DB, TRUNCATE simply does less this cycle and catches up next.
    /// </summary>
    public async Task CheckpointAsync(CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // SQLite serialises writers even in WAL mode; a busy timeout lets a blocked log write
        // wait for the current writer instead of failing. Matches the main DB's interceptor.
        using var pragma = conn.CreateCommand();
        pragma.CommandText = $"PRAGMA busy_timeout={SqliteBusyTimeoutInterceptor.BusyTimeoutMs};";
        pragma.ExecuteNonQuery();

        return conn;
    }
}
