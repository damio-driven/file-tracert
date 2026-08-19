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
    /// Closes a scan by telling the two reasons a row can be missing from it apart (§4/§6):
    /// a row inside one of the <paramref name="skipped"/> areas is one the scan deliberately
    /// did not look at, so it is flagged <c>IsIncluded = false</c> and its
    /// <see cref="FileEntry.IsPresent"/> is left exactly as it was; every other included row
    /// not touched since <paramref name="scanStartedUtc"/> is one the scan looked for and did
    /// not find, so it is flagged <c>IsPresent = false</c>. Soft both ways — never a delete.
    /// <para>Note what "left as it was" implies: while a row is excluded, nothing maintains its
    /// presence either, because no scan looks at it. A file deleted from disk while it sits
    /// outside the perimeter keeps <c>IsPresent = true</c> until a scan covers it again.</para>
    /// <para>Rows already excluded by the file-type filter are left alone in both passes: the
    /// scan never sees them, so "not touched" says nothing about whether they are still on
    /// disk.</para>
    /// </summary>
    Task<ScanClosureResult> ReconcileUnseenFilesAsync(
        int volumeId,
        DateTime scanStartedUtc,
        IReadOnlyCollection<SkippedScanArea> skipped,
        CancellationToken ct);
}

/// <summary>
/// One place a scan deliberately did not look at, addressed the way the file rows are:
/// a whole directory (<paramref name="FileName"/> null — it sits outside the active watched
/// roots, or under a folder the filter excluded) or a single file inside a directory the scan
/// did visit (its own attributes or its path excluded it).
/// </summary>
/// <remarks>
/// Directory ids, not path prefixes. The catalog only ever contains what was once inside the
/// perimeter, so "which catalog directories are now outside it" is normally the empty set and at
/// worst the subtree that just left — while the set of excluded PATHS is large by construction
/// (on a system volume every folder under <c>Windows\</c> fails the filter on its own).
/// </remarks>
public readonly record struct SkippedScanArea(int DirectoryId, string? FileName);

/// <summary>What closing a scan changed.</summary>
/// <param name="Excluded">Rows the scan skipped on purpose → <c>IsIncluded = false</c>.</param>
/// <param name="Absent">Rows the scan looked for and did not find → <c>IsPresent = false</c>.</param>
public readonly record struct ScanClosureResult(int Excluded, int Absent);

/// <summary>Outcome of one merged batch.</summary>
/// <param name="Inserted">Rows the batch added to the catalog.</param>
/// <param name="Updated">Rows the batch found again and refreshed.</param>
/// <param name="AffectedFileIds">Ids of every row the batch touched — what the caller
/// needs to keep the search index in step without re-reading the volume.</param>
public sealed record ScanMergeBatchResult(int Inserted, int Updated, IReadOnlyList<int> AffectedFileIds);
