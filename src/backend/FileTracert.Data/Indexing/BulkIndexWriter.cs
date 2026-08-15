using EFCore.BulkExtensions;
using FileTracert.Data.Entities;

namespace FileTracert.Data.Indexing;

/// <summary>
/// <see cref="IBulkIndexWriter"/> over EFCore.BulkExtensions (SQLite). Bulk
/// operations skip <see cref="SaveChanges"/> and therefore the auditing
/// interceptor, so this class stamps the row-audit fields itself.
/// </summary>
public sealed partial class BulkIndexWriter : IBulkIndexWriter
{
    private const int BatchSize = 20_000;

    private readonly FileTracertDbContext _db;

    public BulkIndexWriter(FileTracertDbContext db)
    {
        _db = db;
    }

    public async Task BulkInsertDirectoriesAsync(IReadOnlyCollection<DirectoryNode> nodes, CancellationToken ct)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        StampAudit(nodes, DateTime.UtcNow);
        var config = new BulkConfig { SetOutputIdentity = true, BatchSize = BatchSize };
        await _db.BulkInsertAsync(nodes.ToList(), config, cancellationToken: ct);
    }

    public async Task BulkInsertFilesAsync(IReadOnlyCollection<FileEntry> files, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return;
        }

        StampAudit(files, DateTime.UtcNow);
        await _db.BulkInsertAsync(files.ToList(), new BulkConfig { BatchSize = BatchSize }, cancellationToken: ct);
    }

    public async Task BulkUpsertFilesAsync(IReadOnlyCollection<FileEntry> files, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // SQLite cannot mix insert + update with auto-identity → split the lists.
        var inserts = files.Where(f => f.Id == 0).ToList();
        var updates = files.Where(f => f.Id != 0).ToList();

        if (inserts.Count > 0)
        {
            StampAudit(inserts, now);
            await _db.BulkInsertAsync(inserts, new BulkConfig { BatchSize = BatchSize }, cancellationToken: ct);
        }

        if (updates.Count > 0)
        {
            foreach (var update in updates)
            {
                update.UpdatedUtc = now;
            }

            await _db.BulkUpdateAsync(updates, new BulkConfig { BatchSize = BatchSize }, cancellationToken: ct);
        }
    }

    private static void StampAudit(IEnumerable<IAuditable> items, DateTime now)
    {
        foreach (var item in items)
        {
            if (item.CreatedUtc == default)
            {
                item.CreatedUtc = now;
            }

            item.UpdatedUtc = now;
        }
    }
}
