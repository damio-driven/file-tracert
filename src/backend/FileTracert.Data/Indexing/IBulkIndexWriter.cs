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

    /// <summary>
    /// Merges one batch of scanned files into the volume's index: rows already in the
    /// catalog are updated in place (identity, pending overlay, hashes and the filter's
    /// <see cref="FileEntry.IsIncluded"/> decision are preserved), unseen ones are inserted.
    /// A re-scan must never truncate — the identities are referenced by
    /// <c>OperationJobItems.FileId</c> and the overlay rows carry the queue's projection (§5).
    /// </summary>
    /// <param name="indexedUtc">Stamped on every row the batch touched; the absent pass
    /// uses it as a generation marker.</param>
    Task<ScanMergeBatchResult> MergeScannedFilesAsync(
        int volumeId, IReadOnlyCollection<FileEntry> batch, DateTime indexedUtc, CancellationToken ct);

    /// <summary>
    /// Closes a scan: every included file of the volume not touched since
    /// <paramref name="scanStartedUtc"/> is flagged <c>IsPresent = false</c> (soft, §6).
    /// Rows excluded by the filter are left alone — the scan never sees them, so "not
    /// touched" says nothing about whether they are still on disk.
    /// </summary>
    /// <returns>How many rows were flagged.</returns>
    Task<int> MarkAbsentFilesAsync(int volumeId, DateTime scanStartedUtc, CancellationToken ct);
}

/// <summary>Outcome of one merged batch.</summary>
/// <param name="Inserted">Rows the batch added to the catalog.</param>
/// <param name="Updated">Rows the batch found again and refreshed.</param>
/// <param name="AffectedFileIds">Ids of every row the batch touched — what the caller
/// needs to keep the search index in step without re-reading the volume.</param>
public sealed record ScanMergeBatchResult(int Inserted, int Updated, IReadOnlyList<int> AffectedFileIds);
