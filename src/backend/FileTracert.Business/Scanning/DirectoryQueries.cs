using FileTracert.Contracts.Scanning;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Scanning;

/// <summary>
/// The subtree query, written once (K5). Four call sites used to spell it out by hand with
/// slightly different shapes — one of them forgetting the root row, another forgetting the
/// volume — and each hand-written variant is a chance for the semantics to drift.
///
/// Note on collation: this runs in SQL, where <c>StartsWith</c> becomes a SQLite <c>LIKE</c>
/// (ASCII case-insensitive). <c>==</c> used to disagree with it — the column defaulted to BINARY
/// — which is P2; the column now carries <c>NOCASE</c>, so both halves of this predicate fold
/// case the same way and agree with the in-memory <see cref="ScanPath.Overlaps"/>. Both fold
/// ASCII only: a non-ASCII case variant still reads as a different path, in SQL and in memory
/// alike. Where the answer must be exact, callers still use the single in-memory predicate.
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
