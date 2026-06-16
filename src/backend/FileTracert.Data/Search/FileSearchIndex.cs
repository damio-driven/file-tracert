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
/// Path formula: if <c>Directories.MaterializedPath = ''</c> the path is just
/// the file name; otherwise <c>dir_path \ file_name</c>.
/// Count is capped at 10 000 — large result sets are slow to count fully and
/// the UI shows "10 000+" when totalCount reaches the cap.
/// bm25 score: lower = more relevant, so Relevance sorts ASC.
/// </summary>
public sealed class FileSearchIndex : IFileSearchIndex
{
    private readonly FileTracertDbContext _db;

    public FileSearchIndex(FileTracertDbContext db) => _db = db;

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
        await _db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO FileSearchIndex(rowid, name, path)
            SELECT f.Id,
                   f.Name,
                   CASE WHEN d.MaterializedPath = '' THEN f.Name
                        ELSE d.MaterializedPath || '\' || f.Name END
            FROM Files f
            JOIN Directories d ON d.Id = f.DirectoryId
            WHERE f.VolumeId = {volumeId} AND f.IsIncluded = 1 AND f.IsPresent = 1
            """,
            ct);
    }

    public async Task RebuildAsync(CancellationToken ct)
    {
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM FileSearchIndex;", ct);
        await _db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO FileSearchIndex(rowid, name, path)
            SELECT f.Id,
                   f.Name,
                   CASE WHEN d.MaterializedPath = '' THEN f.Name
                        ELSE d.MaterializedPath || '\' || f.Name END
            FROM Files f
            JOIN Directories d ON d.Id = f.DirectoryId
            WHERE f.IsIncluded = 1 AND f.IsPresent = 1
            """,
            ct);
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
            var countSql =
                $"""
                SELECT MIN(COUNT(*), 10000)
                FROM FileSearchIndex fts
                JOIN Files f ON f.Id = fts.rowid
                JOIN Volumes v ON v.Id = f.VolumeId
                WHERE fts MATCH $match
                  AND f.IsIncluded = 1 AND f.IsPresent = 1
                {filterSql}
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
            var sortExpr = query.Sort switch
            {
                SearchSort.Name => "f.Name",
                SearchSort.Date => "f.ModifiedUtc",
                SearchSort.Size => "f.SizeBytes",
                _              => "bm25(fts)",
            };
            var sortDir = query.Sort == SearchSort.Relevance ? "ASC" : (query.Desc ? "DESC" : "ASC");

            var pageSql =
                $"""
                SELECT fts.rowid
                FROM FileSearchIndex fts
                JOIN Files f ON f.Id = fts.rowid
                JOIN Volumes v ON v.Id = f.VolumeId
                WHERE fts MATCH $match
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
    /// </summary>
    private static string BuildMatchTerm(string text, SearchScope scope)
    {
        var sanitized = text.Replace("\"", "\"\"").Trim();
        if (string.IsNullOrEmpty(sanitized))
            return "*";

        return scope == SearchScope.Name
            ? $"name : \"{sanitized}*\""
            : $"\"{sanitized}*\"";
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
            p.Add(("$modFrom", q.ModifiedFrom.Value.ToString("o")));
        }
        if (q.ModifiedTo.HasValue)
        {
            sb.AppendLine("  AND f.ModifiedUtc <= $modTo");
            p.Add(("$modTo", q.ModifiedTo.Value.ToString("o")));
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
}
