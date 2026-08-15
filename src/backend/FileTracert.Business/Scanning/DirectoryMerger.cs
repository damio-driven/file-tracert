using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Scanning;

/// <summary>One directory as the scan saw it on disk.</summary>
public readonly record struct ScannedDirectory(string Path, long? UsnFileRef);

/// <param name="IdByPath">Every directory of the volume the scan can address, by relative
/// path — what the file merge needs to resolve each file's <c>DirectoryId</c>.</param>
public sealed record DirectoryMergeResult(
    IReadOnlyDictionary<string, int> IdByPath, int Inserted, int Revived, int MarkedAbsent);

/// <summary>
/// Merges the scanned directory tree into the catalog, preserving row identities and the
/// pending overlay. Directories are the "few rows" side of a scan (a small percentage of the
/// file count) and their parent/child wiring is inherently ordered, so the reconciliation is
/// done here in Business with EF rather than set-based in SQL like the file merge — the
/// bounded structure the scan already holds in memory is the whole tree anyway.
/// </summary>
/// <remarks>
/// Work is committed in short chunks. A scan of a system drive used to hold SQLite's single
/// write lock for minutes, starving the sync worker and the API with SQLITE_BUSY; every unit
/// here is small enough that another writer only ever waits for one chunk.
/// </remarks>
public sealed class DirectoryMerger
{
    private readonly FileTracertDbContext _db;
    private readonly IBulkIndexWriter _bulk;
    private readonly ILogger<DirectoryMerger> _logger;

    public DirectoryMerger(FileTracertDbContext db, IBulkIndexWriter bulk, ILogger<DirectoryMerger> logger)
    {
        _db = db;
        _bulk = bulk;
        _logger = logger;
    }

    public async Task<DirectoryMergeResult> MergeAsync(
        int volumeId, IReadOnlyCollection<ScannedDirectory> scanned, int batchSize, CancellationToken ct)
    {
        var existing = await LoadExistingAsync(volumeId, ct);

        var idByPath = new Dictionary<string, int>(existing.Count + scanned.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var row in existing.Values)
        {
            idByPath[row.Path] = row.Id;
        }

        var seen = new HashSet<string>(scanned.Count, StringComparer.OrdinalIgnoreCase);
        var toInsert = new List<ScannedDirectory>();
        var toRevive = new List<(int Id, long? Frn)>();

        foreach (var dir in scanned)
        {
            seen.Add(dir.Path);

            if (!existing.TryGetValue(dir.Path, out var row))
            {
                toInsert.Add(dir);
                continue;
            }

            // Found again on disk: it is present and materialized, and it may have gained a
            // file reference (first USN scan after an enumeration one). Nothing else about an
            // existing row is a scan's business — Name, ParentId and the overlay stay put.
            var needsFrn = dir.UsnFileRef is not null && row.UsnFileRef != dir.UsnFileRef;
            if (!row.IsPresent || !row.IsMaterialized || needsFrn)
            {
                toRevive.Add((row.Id, needsFrn ? dir.UsnFileRef : null));
            }
        }

        var inserted = await InsertMissingAsync(volumeId, toInsert, idByPath, batchSize, ct);
        var revived = await ReviveAsync(toRevive, batchSize, ct);
        var absent = await MarkAbsentAsync(existing.Values.Where(r => r.IsPresent && !seen.Contains(r.Path)), batchSize, ct);

        if (inserted > 0 || revived > 0 || absent > 0)
        {
            _logger.LogInformation(
                "Volume {VolumeId} directory merge: {Inserted} new, {Revived} refreshed, {Absent} no longer on disk.",
                volumeId, inserted, revived, absent);
        }

        return new DirectoryMergeResult(idByPath, inserted, revived, absent);
    }

    // ── steps ─────────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, ExistingDirectory>> LoadExistingAsync(int volumeId, CancellationToken ct)
    {
        var rows = await _db.Directories
            .AsNoTracking()
            .Where(d => d.VolumeId == volumeId)
            .Select(d => new ExistingDirectory(d.Id, d.MaterializedPath, d.IsPresent, d.IsMaterialized, d.UsnFileRef))
            .ToListAsync(ct);

        var map = new Dictionary<string, ExistingDirectory>(rows.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // Case-insensitive on purpose (Windows semantics). A pre-existing catalog could
            // hold two rows differing only by case; keep the first and let the duplicate be
            // marked absent rather than crashing the scan on it.
            map.TryAdd(row.Path, row);
        }

        return map;
    }

    /// <summary>
    /// Inserts the directories the catalog has never seen, shallowest first so a child's
    /// <c>ParentId</c> always resolves against a row that already has an identity.
    /// </summary>
    private async Task<int> InsertMissingAsync(
        int volumeId, List<ScannedDirectory> toInsert, Dictionary<string, int> idByPath,
        int batchSize, CancellationToken ct)
    {
        if (toInsert.Count == 0)
        {
            return 0;
        }

        var inserted = 0;
        foreach (var level in toInsert.GroupBy(d => Depth(d.Path)).OrderBy(g => g.Key))
        {
            foreach (var chunk in level.Chunk(batchSize))
            {
                ct.ThrowIfCancellationRequested();

                var nodes = new List<DirectoryNode>(chunk.Length);
                foreach (var dir in chunk)
                {
                    int? parentId = null;
                    if (dir.Path.Length > 0)
                    {
                        var parentPath = ScanPath.Parent(dir.Path);
                        if (!idByPath.TryGetValue(parentPath, out var resolved))
                        {
                            throw new InvalidOperationException(
                                $"Directory '{dir.Path}' on volume {volumeId} has no parent row for '{parentPath}': " +
                                "the scanned tree must contain every ancestor.");
                        }

                        parentId = resolved;
                    }

                    nodes.Add(new DirectoryNode
                    {
                        VolumeId = volumeId,
                        ParentId = parentId,
                        Name = dir.Path.Length == 0 ? string.Empty : ScanPath.Name(dir.Path),
                        MaterializedPath = dir.Path,
                        UsnFileRef = dir.UsnFileRef,
                        IsMaterialized = true,
                        IsPresent = true,
                    });
                }

                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await _bulk.BulkInsertDirectoriesAsync(nodes, ct);
                await tx.CommitAsync(ct);

                foreach (var node in nodes)
                {
                    idByPath[node.MaterializedPath] = node.Id;
                }

                inserted += nodes.Count;
            }
        }

        return inserted;
    }

    private async Task<int> ReviveAsync(List<(int Id, long? Frn)> toRevive, int batchSize, CancellationToken ct)
    {
        if (toRevive.Count == 0)
        {
            return 0;
        }

        var revived = 0;
        foreach (var chunk in toRevive.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();

            var ids = chunk.Select(c => c.Id).ToList();
            var frnById = chunk.Where(c => c.Frn is not null).ToDictionary(c => c.Id, c => c.Frn!.Value);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var rows = await _db.Directories.Where(d => ids.Contains(d.Id)).ToListAsync(ct);
            foreach (var row in rows)
            {
                row.IsPresent = true;
                row.IsMaterialized = true;
                if (frnById.TryGetValue(row.Id, out var frn))
                {
                    row.UsnFileRef = frn;
                }
            }

            revived += await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _db.ChangeTracker.Clear();
        }

        return revived;
    }

    /// <summary>
    /// Flags the directories the scan no longer found. Soft only (§6): the row and every
    /// <c>Pending*</c> field stay, because a queued operation may still reference it.
    /// </summary>
    private async Task<int> MarkAbsentAsync(
        IEnumerable<ExistingDirectory> absent, int batchSize, CancellationToken ct)
    {
        var marked = 0;
        foreach (var chunk in absent.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();

            var ids = chunk.Select(c => c.Id).ToList();
            var now = DateTime.UtcNow;

            // ExecuteUpdate bypasses SaveChanges and therefore the auditing interceptor,
            // so the row-audit stamp is written here explicitly.
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            marked += await _db.Directories
                .Where(d => ids.Contains(d.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.IsPresent, false)
                    .SetProperty(d => d.UpdatedUtc, now), ct);
            await tx.CommitAsync(ct);
        }

        return marked;
    }

    private static int Depth(string path) => path.Length == 0 ? 0 : path.Count(c => c == '\\') + 1;

    private sealed record ExistingDirectory(int Id, string Path, bool IsPresent, bool IsMaterialized, long? UsnFileRef);
}
