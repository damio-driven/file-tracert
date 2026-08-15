using FileTracert.Contracts.Paging;

namespace FileTracert.Contracts.Search;

/// <summary>
/// Port interface for the FTS5 full-text search index. Implemented in Data (SQLite-specific).
/// Sync is explicit — no triggers. Called by ScanService within the scan transaction.
/// </summary>
public interface IFileSearchIndex
{
    /// <summary>Delete FTS entries for a volume's files. Call BEFORE deleting Files rows.</summary>
    Task ClearVolumeAsync(int volumeId, CancellationToken ct);

    /// <summary>Insert FTS entries from the Files table for a volume. Call AFTER bulk insert.</summary>
    Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct);

    /// <summary>Full rebuild of the FTS index from all Files in the DB. Used for one-time backfill.</summary>
    Task RebuildAsync(CancellationToken ct);

    /// <summary>
    /// Re-syncs the entries of exactly these files: each is removed and re-added from its
    /// current row, so calling it twice is a no-op rather than a duplicate. This is how a scan
    /// keeps the index in step batch by batch, instead of rebuilding the whole volume once the
    /// rows have settled. Ids the catalog no longer includes or no longer has on disk end up
    /// removed, which is the correct outcome for them too.
    /// </summary>
    Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct);

    /// <summary>
    /// Drops the entries of every file of the volume that is excluded or no longer on disk.
    /// Run at the end of a scan, after the absent pass has flagged them.
    /// </summary>
    Task PruneVolumeAsync(int volumeId, CancellationToken ct);

    /// <summary>Single-file upsert — for incremental USN updates (step 10).</summary>
    Task UpsertAsync(int fileId, string name, string path, CancellationToken ct);

    /// <summary>Single-file remove — for incremental USN deletes (step 10).</summary>
    Task RemoveAsync(int fileId, CancellationToken ct);

    /// <summary>FTS5 MATCH + structural filters + paging. Returns file IDs and total count.</summary>
    Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct);
}
