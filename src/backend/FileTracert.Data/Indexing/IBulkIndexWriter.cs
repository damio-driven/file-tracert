using FileTracert.Data.Entities;

namespace FileTracert.Data.Indexing;

/// <summary>
/// High-throughput persistence seam for the indexing hot path. Lives in Data
/// (its signatures reference the EF entities, which Contracts cannot see) but is
/// consumed by Business through this interface, keeping the SQLite/bulk quirks
/// isolated and the storage provider swappable.
/// </summary>
/// <remarks>
/// All methods participate in the DbContext's current transaction when one is
/// open, and bypass the auditing interceptor — implementations stamp the
/// row-audit fields themselves.
/// </remarks>
public interface IBulkIndexWriter
{
    /// <summary>Bulk-inserts directory nodes, writing back generated identities.</summary>
    Task BulkInsertDirectoriesAsync(IReadOnlyCollection<DirectoryNode> nodes, CancellationToken ct);

    /// <summary>Bulk-inserts files (first full scan: target table is empty for the volume).</summary>
    Task BulkInsertFilesAsync(IReadOnlyCollection<FileEntry> files, CancellationToken ct);

    /// <summary>
    /// Bulk upsert for the incremental path. SQLite cannot combine insert+update
    /// with auto-identity, so the lists are split by identity inside.
    /// </summary>
    Task BulkUpsertFilesAsync(IReadOnlyCollection<FileEntry> files, CancellationToken ct);
}
