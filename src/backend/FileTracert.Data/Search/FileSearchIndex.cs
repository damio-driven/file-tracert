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
///
/// The third column, <c>tags</c>, holds no words — see <see cref="FileSearchTags"/>. It is what
/// lets a category or volume filter be answered by the index instead of by resolving every match
/// on <c>Files</c>, and it is never reachable from user input: every MATCH built here is scoped to
/// <c>{name}</c> or <c>{name path}</c>.
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
        INSERT INTO FileSearchIndex(rowid, name, path, tags)
        SELECT f.Id,
               {ProjectedNameSql},
               {ProjectedPathSql},
               {FileSearchTags.SqlExpression}
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
    /// A real COUNT, walking the whole index. It was an EXISTS while the caller only asked "is it
    /// empty"; 14a showed that question was the wrong one (see <see cref="IFileSearchIndex"/>), and
    /// an EXISTS cannot answer the right one. Run through the raw connection because
    /// <c>FileSearchIndex</c> is an FTS5 virtual table with no entity behind it — which is
    /// precisely why this belongs on this side of the boundary (K12).
    /// </summary>
    public async Task<long> CountEntriesAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM FileSearchIndex";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
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

    public async Task SyncDirectoriesAsync(IReadOnlyCollection<int> directoryIds, CancellationToken ct)
    {
        if (directoryIds.Count == 0)
        {
            return;
        }

        foreach (var chunk in directoryIds.Chunk(IdChunkSize))
        {
            var list = string.Join(", ", chunk);

            // The row set is named by directory and never crosses the boundary: the DELETE finds
            // the affected entries through Files itself (a seek on the leading column of
            // IX_Files_DirectoryId_PendingDirectoryId_IsIncluded_IsPresent), and the INSERT rebuilds
            // exactly the includable ones. Ids inlined for the same reason as SyncFilesAsync —
            // ints, nothing to escape, and SQLite has no array-valued parameter.
            var deleteSql =
                "DELETE FROM FileSearchIndex WHERE rowid IN " +
                "(SELECT Id FROM Files WHERE DirectoryId IN (" + list + "))";
            var insertSql = $"{InsertProjectedSql} WHERE f.DirectoryId IN ({list}) AND {IndexableSql}";

            // Delete first, always — FTS5 has no ON CONFLICT (same rule as SyncFilesAsync).
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

        // Name and path are the caller's — it is finishing a rename or a move and knows where the
        // file landed. The tags are NOT: they are facts of the row, and the caller has just saved
        // it, so they are read back from Files as a scalar subquery. A tag column left empty here
        // would not be a slow search, it would be a moved file that a category-filtered search
        // stops finding until the next scan.
        //
        // Built as a local and run raw: the tag expression is SQL, and an interpolated
        // ExecuteSqlAsync hole would bind it as a string literal instead. The caller's values stay
        // real parameters ({0}, {1}, {2}).
        const string insertSql =
            $$"""
            INSERT INTO FileSearchIndex(rowid, name, path, tags)
            VALUES ({0}, {1}, {2},
                    (SELECT {{FileSearchTags.SqlExpression}} FROM Files f WHERE f.Id = {0}))
            """;
        await _db.Database.ExecuteSqlRawAsync(insertSql, [fileId, name, path], ct);
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
        var matchTerm = BuildMatchTerm(query);

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
            //
            // 14a — CROSS JOIN, not JOIN, and it is not decoration: in SQLite it forbids the
            // planner from reordering the loops, which is the whole point. Given an equality
            // predicate on an indexed column of Files (an extension filter is what is left that
            // can do it), the planner would otherwise drive from that index and ask the FTS table
            // "does rowid X match?" once per candidate row — and each of those questions re-runs
            // the full-text query, which for a prefix term means merging its whole doclist again.
            // The cost stops being "match set times a constant" and becomes "match set times the
            // query, again": on the real catalog that turned a filter matching nothing into 739 ms,
            // and the same shape on the category filter never returned at all.
            //
            // Pinning gives up the theoretical case where driving from Files is genuinely better.
            // It is not a real loss: that shape only pays off when SQLite can be trusted to know
            // the selectivity, and this database never runs ANALYZE (see step 11e) — so what it is
            // really choosing on is a default guess, against a virtual table whose per-probe cost
            // it cannot see at all.
            var countSql =
                $"""
                SELECT COUNT(*) FROM (
                  SELECT 1
                  FROM FileSearchIndex fts
                  CROSS JOIN Files f ON f.Id = fts.rowid
                  CROSS JOIN Volumes v ON v.Id = f.VolumeId
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
            //
            // The weights are per column, in declaration order — name, path, tags. The third is
            // ZERO on purpose: the tags column carries synthetic tokens, and letting a matched
            // `ftcimage` contribute to the score would make the relevance order of a filtered
            // search differ from the same search unfiltered, for a reason no user could see.
            var sortExpr = query.Sort switch
            {
                SearchSort.Name => "f.Name",
                SearchSort.Date => "f.ModifiedUtc",
                SearchSort.Size => "f.SizeBytes",
                _              => "bm25(FileSearchIndex, 1.0, 1.0, 0.0)",
            };
            var sortDir = query.Sort == SearchSort.Relevance ? "ASC" : (query.Desc ? "DESC" : "ASC");

            var pageSql =
                $"""
                SELECT fts.rowid
                FROM FileSearchIndex fts
                CROSS JOIN Files f ON f.Id = fts.rowid
                CROSS JOIN Volumes v ON v.Id = f.VolumeId
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
    /// Builds the FTS5 MATCH expression: the user's text, plus the structural filters that live in
    /// the index (§14a), ANDed together so the index does the intersection itself.
    ///
    /// <para>The text half is always column-scoped — <c>{name}</c> for a name search,
    /// <c>{name path}</c> for a full-path one. Scoping the full-path case is what keeps the
    /// <c>tags</c> column out of reach of user input: an unscoped MATCH searches EVERY column, so
    /// typing <c>ftcimage</c> would otherwise select every image. On the two columns that existed
    /// before, <c>{name path} : x</c> and a bare <c>x</c> mean exactly the same thing, so nothing
    /// the user could search for changes its answer.</para>
    ///
    /// <para>An empty text contributes no conjunct at all; if the query has no tags either, the
    /// expression falls back to <c>*</c>, which matches every row (callers should avoid an empty
    /// query, but this stays a safe fallback rather than a syntax error).</para>
    ///
    /// IMPORTANT — FTS5 phrase-prefix syntax: the asterisk must appear OUTSIDE the
    /// closing double-quote (<c>"term"*</c>), NOT inside (<c>"term*"</c>).
    /// Inside quotes, <c>*</c> is a literal character; outside, it is the prefix
    /// operator. See https://www.sqlite.org/fts5.html#full_text_query_syntax.
    /// </summary>
    private static string BuildMatchTerm(FileSearchQuery q)
    {
        var conjuncts = new List<string>(3);

        // Escape embedded double-quotes by doubling them (FTS5 convention).
        var sanitized = q.Text.Replace("\"", "\"\"").Trim();
        if (sanitized.Length > 0)
        {
            var columns = q.Scope == SearchScope.Name ? "{name}" : "{name path}";
            // Asterisk is placed OUTSIDE the closing quote for prefix matching.
            conjuncts.Add($"{columns} : \"{sanitized}\"*");
        }

        if (q.Category.HasValue)
            conjuncts.Add($"{FileSearchTags.Column} : {FileSearchTags.Category(q.Category.Value)}");

        if (q.VolumeId.HasValue)
            conjuncts.Add($"{FileSearchTags.Column} : {FileSearchTags.Volume(q.VolumeId.Value)}");

        return conjuncts.Count == 0 ? "*" : string.Join(" AND ", conjuncts);
    }

    /// <summary>
    /// Builds the additional WHERE clauses (ANDed after the FTS MATCH predicate)
    /// plus the matching SqliteParameter list. Extensions are inlined after
    /// sanitisation because SQLite does not support array-valued parameters.
    ///
    /// <para>Category and volume are deliberately absent: since 14a they are answered inside the
    /// MATCH by <see cref="FileSearchTags"/>, which is what makes their cost follow the result
    /// instead of the match set. What remains here is what cannot become a token — a range
    /// (size, date), a value whose text does not survive the tokenizer intact (extension), or a
    /// fact of another table (<c>OnlineOnly</c>).</para>
    /// </summary>
    private static (string Sql, List<(string Name, object Value)> Params) BuildFilterClause(FileSearchQuery q)
    {
        var sb = new StringBuilder();
        var p = new List<(string, object)>();

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
