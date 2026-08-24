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
    /// How many entries the index holds — what the startup backfill needs in order to decide
    /// whether <see cref="RebuildAsync"/> is owed.
    ///
    /// <para>It used to be <c>IsEmptyAsync</c>, and "is there at least one row" turned out to be
    /// the wrong question (step 14a). An index can be non-empty and still WRONG: the 14a migration
    /// leaves it empty for the backfill to refill, and before the backfill runs, the startup
    /// orphan-overlay reconciliation re-syncs the handful of files whose overlay it just cleared.
    /// Under the old guard those few rows made the index "not empty" and the rebuild was skipped —
    /// for that startup and every one after it — so search answered from a handful of rows with
    /// nothing logged and the Catalog screen still perfectly correct. A rebuild interrupted halfway
    /// leaves the same shape. So the caller compares against the number of rows that BELONG in the
    /// index and rebuilds when the index is short.</para>
    ///
    /// <para>K12 still holds: the implementation keeps the FTS5 specifics (the table name, the
    /// provider), the caller keeps the decision. The cost is a full count of the index — measured
    /// at ~250–335 ms on a 742 033-entry catalog, paid once per startup.</para>
    /// </summary>
    Task<long> CountEntriesAsync(CancellationToken ct);

    /// <summary>
    /// Re-syncs the entries of exactly these files: each is removed and re-added from its
    /// current row, so calling it twice is a no-op rather than a duplicate. This is how a scan
    /// keeps the index in step batch by batch, instead of rebuilding the whole volume once the
    /// rows have settled. Ids the catalog no longer includes or no longer has on disk end up
    /// removed, which is the correct outcome for them too.
    /// </summary>
    Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct);

    /// <summary>
    /// Re-syncs the entries of every file that sits in these directories, without the caller ever
    /// naming a file. Same semantics as <see cref="SyncFilesAsync"/> — each affected entry is
    /// removed and re-added from the current row, so what is no longer includable ends up removed
    /// — but the set is expressed by DIRECTORY, which is what a folder rename or a folder move
    /// actually changed.
    ///
    /// <para>It exists because the alternative costs the wrong thing: to prune stale entries the
    /// caller would have to hand over the ids of ALL the files under the subtree, excluded and
    /// absent ones included, and those are exactly the rows a narrowed filter accumulates in bulk.
    /// A folder holding a million excluded files and a hundred indexed ones would marshal a million
    /// ints to re-sync a hundred entries. Expressed by directory, the work is one pair of
    /// statements per chunk of DIRECTORIES and the row set never leaves the database.</para>
    /// </summary>
    Task SyncDirectoriesAsync(IReadOnlyCollection<int> directoryIds, CancellationToken ct);

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
