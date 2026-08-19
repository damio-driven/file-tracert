using System.Text;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Data.Search;

/// <summary>
/// SQLite FTS5 implementation of <see cref="IFileSearchIndex"/>. All DML goes
/// through raw SQL because EF Core has no FTS5 mapping; SELECT queries use a
/// raw <see cref="SqliteCommand"/> so we can build dynamic WHERE clauses without
/// the risk of LINQ leaking SQLite-specific expressions.
///
/// Row identity: <c>rowid = Files.Id</c>.
///
/// PROJECTED name (§5): the <c>name</c> column holds <c>PendingName ?? Name</c>, so a queued
/// rename is searchable under its new name the moment it is queued and stops answering to the
/// old one. Expressed here as <see cref="ProjectedNameSql"/> — the rule itself lives in
/// <c>FileTracert.Business/Projection/Projected.cs</c>, this is its SQL mirror.
/// The <c>path</c> column is the PHYSICAL directory path joined with that projected name: a
/// queued FOLDER rename deliberately does not touch this index (§5 — no file name changes, and
/// the alternative is tens of thousands of writes per enqueue); the projected path is what the
/// search RESULT shows, computed at read time.
///
/// Path formula: if <c>Directories.MaterializedPath = ''</c> the path is just
/// the projected file name; otherwise <c>dir_path \ projected_name</c>.
/// Count is capped at 10 000 — large result sets are slow to count fully and
/// the UI shows "10 000+" when totalCount reaches the cap.
/// bm25 score: lower = more relevant, so Relevance sorts ASC.
/// </summary>
public sealed class FileSearchIndex : IFileSearchIndex
{
    private readonly FileTracertDbContext _db;

    public FileSearchIndex(FileTracertDbContext db) => _db = db;

    /// <summary>
    /// The projected file name in SQL. <c>NULLIF</c> guards the empty string as well as NULL:
    /// an overlay is either a real new name or absent, never a blank that would erase the row
    /// from every search.
    /// </summary>
    private const string ProjectedNameSql = "COALESCE(NULLIF(f.PendingName, ''), f.Name)";

    /// <summary>Physical directory path + projected file name — the <c>path</c> column.</summary>
    private const string ProjectedPathSql =
        $"CASE WHEN d.MaterializedPath = '' THEN {ProjectedNameSql} " +
        $"ELSE d.MaterializedPath || '\\' || {ProjectedNameSql} END";

    /// <summary>
    /// The insert shared by every population path (per-volume sync, full rebuild, per-batch sync).
    /// One definition so a change to the projected-name rule cannot land on two of the three.
    /// Callers append their own <c>WHERE</c>; the inclusion filter is spelled out by
    /// <see cref="IndexableSql"/>.
    /// </summary>
    private const string InsertProjectedSql =
        $"""
        INSERT INTO FileSearchIndex(rowid, name, path)
        SELECT f.Id,
               {ProjectedNameSql},
               {ProjectedPathSql}
        FROM Files f
        JOIN Directories d ON d.Id = f.DirectoryId
        """;

    /// <summary>Only included, still-present files belong in the index.</summary>
    private const string IndexableSql = "f.IsIncluded = 1 AND f.IsPresent = 1";

    // -------------------------------------------------------------------------
    // Bulk / volume-level operations
    // -------------------------------------------------------------------------

    public async Task ClearVolumeAsync(int volumeId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM FileSearchIndex WHERE rowid IN (SELECT Id FROM Files WHERE VolumeId = {volumeId})",
            ct);
    }

    public async Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct)
    {
        // Built as a local, not interpolated at the call site: the SQL body is a compile-time
        // constant and the only runtime value stays a real parameter ({0}).
        var sql = $"{InsertProjectedSql} WHERE f.VolumeId = {{0}} AND {IndexableSql}";
        await _db.Database.ExecuteSqlRawAsync(sql, [volumeId], ct);
    }

    public async Task RebuildAsync(CancellationToken ct)
    {
        var sql = $"{InsertProjectedSql} WHERE {IndexableSql}";
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM FileSearchIndex;", ct);
        await _db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    /// <summary>
    /// Ids are inlined rather than parameterised: they are <see cref="int"/> values (nothing to
    /// escape) and SQLite has no array-valued parameter, so a parameter per id would hit the
    /// statement's variable ceiling on a full batch. Chunked to keep each statement small.
    /// </summary>
    private const int IdChunkSize = 500;

    public async Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        foreach (var chunk in fileIds.Chunk(IdChunkSize))
        {
            var list = string.Join(", ", chunk);

            // Built as locals, not interpolated at the call site: the id list is the one thing
            // that cannot be parameterised here, and the analyzer (EF1002) rightly objects to
            // interpolation inline. The values are ints straight from the merge — nothing to
            // escape and nothing user-supplied.
            var deleteSql = "DELETE FROM FileSearchIndex WHERE rowid IN (" + list + ")";
            var insertSql = $"{InsertProjectedSql} WHERE f.Id IN ({list}) AND {IndexableSql}";

            // Delete first, always: FTS5 has no ON CONFLICT, and re-syncing a file that is
            // still indexed would otherwise leave two entries for one rowid.
            await _db.Database.ExecuteSqlRawAsync(deleteSql, ct);
            await _db.Database.ExecuteSqlRawAsync(insertSql, ct);
        }
    }

    public async Task PruneVolumeAsync(int volumeId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM FileSearchIndex
             WHERE rowid IN (SELECT Id FROM Files
                              WHERE VolumeId = {volumeId} AND (IsIncluded = 0 OR IsPresent = 0))
            """, ct);
    }

    // -------------------------------------------------------------------------
    // Single-file upsert / remove (for incremental USN updates)
    // -------------------------------------------------------------------------

    public async Task UpsertAsync(int fileId, string name, string path, CancellationToken ct)
    {
        // FTS5 does not support ON CONFLICT; delete first, then reinsert.
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM FileSearchIndex WHERE rowid = {fileId}", ct);
        await _db.Database.ExecuteSqlAsync(
            $"INSERT INTO FileSearchIndex(rowid, name, path) VALUES ({fileId}, {name}, {path})", ct);
    }

    public async Task RemoveAsync(int fileId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM FileSearchIndex WHERE rowid = {fileId}", ct);
    }

    // -------------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------------

    public async Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct)
    {
        var paged = new PagedRequest(query.Skip, query.Take).Normalized();
        var matchTerm = BuildMatchTerm(query.Text, query.Scope);

        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var (filterSql, filterParams) = BuildFilterClause(query);

            // COUNT is capped at 10 000. Large result sets can be very slow to count fully;
            // the UI displays "10 000+" when totalCount == 10 000 and items.Count == take.
            //
            // E3 — the cap is applied by a LIMIT on the SCAN, not by MIN() on the RESULT.
            // `SELECT MIN(COUNT(*), 10000) FROM …` still visits every match and joins two
            // tables for each of them before clamping the number it prints: on a query that
            // matches half the catalog the cap saved the display and nothing else. Wrapping a
            // LIMIT-ed subquery makes SQLite stop stepping at the cap, so the work is bounded
            // by the cap instead of by the size of the match. Same number out — MIN(n, cap)
            // and "count of at most cap rows" agree for every n.
            //
            // NOTE: SQLite FTS5 requires the real table name (not an alias) in the MATCH
            // predicate when the FTS table is joined with other tables. Using the alias
            // causes "no such column: <alias>" on SQLite 3.x.
            var countSql =
                $"""
                SELECT COUNT(*) FROM (
                  SELECT 1
                  FROM FileSearchIndex fts
                  JOIN Files f ON f.Id = fts.rowid
                  JOIN Volumes v ON v.Id = f.VolumeId
                  WHERE FileSearchIndex MATCH $match
                    AND f.IsIncluded = 1 AND f.IsPresent = 1
                  {filterSql}
                  LIMIT 10000
                )
                """;

            int total;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = countSql;
                cmd.Parameters.AddWithValue("$match", matchTerm);
                foreach (var (n, v) in filterParams) cmd.Parameters.AddWithValue(n, v);
                total = Convert.ToInt32((long)(await cmd.ExecuteScalarAsync(ct))!);
            }

            // bm25 lower = more relevant → sort ASC for Relevance; for other sorts
            // honour query.Desc (default ascending).
            // bm25() also requires the real table name (not alias) for the same SQLite reason.
            var sortExpr = query.Sort switch
            {
                SearchSort.Name => "f.Name",
                SearchSort.Date => "f.ModifiedUtc",
                SearchSort.Size => "f.SizeBytes",
                _              => "bm25(FileSearchIndex)",
            };
            var sortDir = query.Sort == SearchSort.Relevance ? "ASC" : (query.Desc ? "DESC" : "ASC");

            var pageSql =
                $"""
                SELECT fts.rowid
                FROM FileSearchIndex fts
                JOIN Files f ON f.Id = fts.rowid
                JOIN Volumes v ON v.Id = f.VolumeId
                WHERE FileSearchIndex MATCH $match
                  AND f.IsIncluded = 1 AND f.IsPresent = 1
                {filterSql}
                ORDER BY {sortExpr} {sortDir}
                LIMIT $take OFFSET $skip
                """;

            var ids = new List<int>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = pageSql;
                cmd.Parameters.AddWithValue("$match", matchTerm);
                foreach (var (n, v) in filterParams) cmd.Parameters.AddWithValue(n, v);
                cmd.Parameters.AddWithValue("$take", paged.Take);
                cmd.Parameters.AddWithValue("$skip", paged.Skip);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    ids.Add(reader.GetInt32(0));
            }

            return new PagedResult<int>(ids, total, paged.Skip, paged.Take);
        }
        finally
        {
            _db.Database.CloseConnection();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the FTS5 MATCH expression. A column filter (<c>name :</c>) restricts
    /// the match to the name column; without it both name and path are searched.
    /// An empty query becomes <c>*</c> which matches all rows (caller should avoid
    /// calling SearchAsync with an empty query, but this is a safe fallback).
    ///
    /// IMPORTANT — FTS5 phrase-prefix syntax: the asterisk must appear OUTSIDE the
    /// closing double-quote (<c>"term"*</c>), NOT inside (<c>"term*"</c>).
    /// Inside quotes, <c>*</c> is a literal character; outside, it is the prefix
    /// operator. See https://www.sqlite.org/fts5.html#full_text_query_syntax.
    /// </summary>
    private static string BuildMatchTerm(string text, SearchScope scope)
    {
        // Escape embedded double-quotes by doubling them (FTS5 convention).
        var sanitized = text.Replace("\"", "\"\"").Trim();
        if (string.IsNullOrEmpty(sanitized))
            return "*";

        // Asterisk is placed OUTSIDE the closing quote for prefix matching.
        return scope == SearchScope.Name
            ? $"name : \"{sanitized}\"*"
            : $"\"{sanitized}\"*";
    }

    /// <summary>
    /// Builds the additional WHERE clauses (ANDed after the FTS MATCH predicate)
    /// plus the matching SqliteParameter list. Extensions are inlined after
    /// sanitisation because SQLite does not support array-valued parameters.
    /// </summary>
    private static (string Sql, List<(string Name, object Value)> Params) BuildFilterClause(FileSearchQuery q)
    {
        var sb = new StringBuilder();
        var p = new List<(string, object)>();

        if (q.Category.HasValue)
        {
            sb.AppendLine("  AND f.Category = $category");
            p.Add(("$category", q.Category.Value.ToString()));
        }
        if (q.Extensions is { Length: > 0 })
        {
            // Extensions are lowercase alphanumeric — sanitise single quotes and inline.
            var list = string.Join(", ", q.Extensions
                .Select(e => $"'{e.Replace("'", "''").ToLowerInvariant()}'"));
            sb.AppendLine($"  AND f.Extension IN ({list})");
        }
        if (q.SizeBytesMin.HasValue)
        {
            sb.AppendLine("  AND f.SizeBytes >= $szMin");
            p.Add(("$szMin", q.SizeBytesMin.Value));
        }
        if (q.SizeBytesMax.HasValue)
        {
            sb.AppendLine("  AND f.SizeBytes <= $szMax");
            p.Add(("$szMax", q.SizeBytesMax.Value));
        }
        if (q.ModifiedFrom.HasValue)
        {
            sb.AppendLine("  AND f.ModifiedUtc >= $modFrom");
            p.Add(("$modFrom", AsUtc(q.ModifiedFrom.Value)));
        }
        if (q.ModifiedTo.HasValue)
        {
            sb.AppendLine("  AND f.ModifiedUtc <= $modTo");
            p.Add(("$modTo", AsUtc(q.ModifiedTo.Value)));
        }
        if (q.VolumeId.HasValue)
        {
            sb.AppendLine("  AND f.VolumeId = $volId");
            p.Add(("$volId", q.VolumeId.Value));
        }
        if (q.OnlineOnly)
        {
            sb.AppendLine("  AND v.IsOnline = 1");
        }

        return (sb.ToString(), p);
    }

    /// <summary>
    /// Normalises a bound to UTC and hands it over as a <see cref="DateTime"/>, not as a
    /// string: the provider then writes it in the same TEXT layout it used for the column
    /// (<c>yyyy-MM-dd HH:mm:ss.FFFFFFF</c>). An ISO round-trip string ("…T14:20:29.912Z")
    /// compares lexically against that layout and loses — ' ' (0x20) sorts before 'T' (0x54)
    /// — so a midnight lower bound used to drop the entire day (review finding #11).
    /// Comparing on <c>julianday()</c> would be format-proof but would forfeit the index on
    /// ModifiedUtc, which also serves the Date sort.
    /// </summary>
    private static DateTime AsUtc(DateTime bound) => bound.Kind switch
    {
        DateTimeKind.Utc => bound,
        DateTimeKind.Local => bound.ToUniversalTime(),
        _ => DateTime.SpecifyKind(bound, DateTimeKind.Utc),
    };
}
