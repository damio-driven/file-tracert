using FileTracert.Business.Filtering;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Setup;

/// <summary>
/// Reconciles the existing index to a changed file-type filter <em>without</em> a
/// rescan or any delete (CLAUDE.md §4): flips <see cref="FileEntry.IsIncluded"/>
/// on already-indexed rows by extension membership. Widening the filter cannot
/// resurrect never-indexed types, so the result flags <c>NeedsScan</c>.
/// </summary>
public sealed class FilterReconciler
{
    private readonly FileTracertDbContext _db;

    public FilterReconciler(FileTracertDbContext db) => _db = db;

    /// <summary>
    /// True when <paramref name="next"/> lets through something <paramref name="previous"/> did
    /// not: an extension it now allows (empty allow-list = "all", the widest), or a path segment
    /// it no longer excludes. Narrowing on both counts → false.
    ///
    /// <para>The answer means "a scan is needed": widening cannot resurrect what was never
    /// indexed. Dropping an excluded path segment is a widening for exactly that reason — the
    /// rows under it were never written — and reading only the allow-list said "no scan needed"
    /// while nothing on disk had been looked at.</para>
    /// </summary>
    public static bool FilterWidened(EffectiveFilter previous, EffectiveFilter next) =>
        ExtensionsWidened(previous, next) || PathExclusionsRelaxed(previous, next);

    private static bool ExtensionsWidened(EffectiveFilter previous, EffectiveFilter next)
    {
        if (next.AllowedExtensions.Count == 0)
        {
            return previous.AllowedExtensions.Count != 0;
        }

        if (previous.AllowedExtensions.Count == 0)
        {
            return false;
        }

        return next.AllowedExtensions.Any(e => !previous.AllowedExtensions.Contains(e));
    }

    private static bool PathExclusionsRelaxed(EffectiveFilter previous, EffectiveFilter next)
    {
        if (previous.ExcludedPathSegments.Count == 0)
        {
            return false;
        }

        var kept = new HashSet<string>(next.ExcludedPathSegments, StringComparer.OrdinalIgnoreCase);
        return previous.ExcludedPathSegments.Any(segment => !kept.Contains(segment));
    }

    /// <summary>
    /// Recomputes inclusion for every file under <paramref name="root"/> against the
    /// resolved <paramref name="effective"/> filter. Returns the new included/excluded
    /// counts. Caller owns the transaction.
    /// </summary>
    public async Task<(int Included, int Excluded)> ReconcileRootAsync(
        WatchedRoot root,
        EffectiveFilter effective,
        CancellationToken ct)
    {
        var files = FilesUnder(root);

        int included;
        int excluded;

        if (effective.AllowedExtensions.Count == 0)
        {
            included = await files.ExecuteUpdateAsync(s => s.SetProperty(f => f.IsIncluded, true), ct);
            excluded = 0;
        }
        else
        {
            var allowed = effective.AllowedExtensions.ToList();
            included = await files.Where(f => allowed.Contains(f.Extension))
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsIncluded, true), ct);
            excluded = await files.Where(f => !allowed.Contains(f.Extension))
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsIncluded, false), ct);
        }

        return (included, excluded);
    }

    /// <summary>Marks every file under <paramref name="root"/> as excluded (soft delete on root removal).</summary>
    public Task<int> ExcludeAllUnderAsync(WatchedRoot root, CancellationToken ct) =>
        FilesUnder(root).ExecuteUpdateAsync(s => s.SetProperty(f => f.IsIncluded, false), ct);

    private IQueryable<FileEntry> FilesUnder(WatchedRoot root)
    {
        var prefix = WatchedRootPath.Normalize(root.RelativePath);
        var query = _db.Files.Where(f => f.VolumeId == root.VolumeId);

        if (prefix.Length == 0)
        {
            return query;
        }

        var prefixWithSep = prefix + "\\";
        return query.Where(f =>
            f.Directory.MaterializedPath == prefix ||
            f.Directory.MaterializedPath.StartsWith(prefixWithSep));
    }
}
