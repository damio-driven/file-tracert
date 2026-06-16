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

    /// <summary>Single-file upsert — for incremental USN updates (step 10).</summary>
    Task UpsertAsync(int fileId, string name, string path, CancellationToken ct);

    /// <summary>Single-file remove — for incremental USN deletes (step 10).</summary>
    Task RemoveAsync(int fileId, CancellationToken ct);

    /// <summary>FTS5 MATCH + structural filters + paging. Returns file IDs and total count.</summary>
    Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct);
}
