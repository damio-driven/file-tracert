using FileTracert.Data.Entities;

namespace FileTracert.Business.Scanning;

/// <summary>
/// The subtree query, written once (K5). Four call sites used to spell it out by hand with
/// slightly different shapes — one of them forgetting the root row, another forgetting the
/// volume — and each hand-written variant is a chance for the semantics to drift.
///
/// Note on collation: this runs in SQL, where <c>StartsWith</c> becomes a SQLite <c>LIKE</c>
/// (ASCII case-insensitive) while <c>==</c> uses the column's BINARY collation. That mismatch
/// is tolerable here — the paths compared come from the catalog itself, so their casing is the
/// casing the scanner recorded. Where the answer must be exact, callers use the single
/// in-memory predicate <see cref="ScanPath.Overlaps"/> instead.
/// </summary>
public static class DirectoryQueries
{
    /// <summary>
    /// Every directory row of <paramref name="volumeId"/> at or below <paramref name="rootPath"/>.
    /// </summary>
    /// <param name="includeRoot">False to keep only the strict descendants.</param>
    public static IQueryable<DirectoryNode> InSubtree(
        this IQueryable<DirectoryNode> source, int volumeId, string rootPath, bool includeRoot = true)
    {
        var prefix = ScanPath.SubtreePrefix(rootPath);

        return includeRoot
            ? source.Where(d => d.VolumeId == volumeId &&
                                (d.MaterializedPath == rootPath || d.MaterializedPath.StartsWith(prefix)))
            : source.Where(d => d.VolumeId == volumeId && d.MaterializedPath.StartsWith(prefix));
    }
}
