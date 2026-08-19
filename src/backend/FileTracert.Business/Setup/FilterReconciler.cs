using FileTracert.Business.Filtering;
using FileTracert.Contracts.Scanning;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Setup;

/// <summary>
/// Reconciles the existing index to a changed filter or perimeter <em>without</em> a rescan or any
/// delete (CLAUDE.md §4) — and, since step 11h, only for the causes it is entitled to undo.
///
/// <para>A row records WHY it is out: <c>ExcludedByType</c> (the extension is off the allow-list),
/// <c>ExcludedByRoot</c> (no active watched root governs it) or <c>ExcludedByScan</c> (the scan
/// stepped over it — hidden folder, excluded path segment). The first two are facts of the
/// settings and are recomputed here from scratch; the third is a fact of the disk, and nothing
/// short of another scan may retract it. Before that distinction existed, widening the type filter
/// re-included the content of a folder the user had hidden, until the next scan pushed it back
/// out.</para>
///
/// <para>Widening cannot resurrect never-indexed rows, so the result still flags
/// <c>NeedsScan</c>.</para>
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
    /// Recomputes inclusion for every file under <paramref name="root"/> against the resolved
    /// <paramref name="effective"/> filter and the root's own <see cref="WatchedRoot.IsActive"/>,
    /// leaving <c>ExcludedByScan</c> exactly as the last scan left it. Returns the new
    /// included/excluded counts. Caller owns the transaction.
    ///
    /// <para>An inactive root is handled here rather than by the caller: it is the same question
    /// ("which of the two settings-borne causes apply to these rows?"), and answering it in two
    /// places is how the global filter change used to quietly re-include the content of a root the
    /// user had switched off.</para>
    /// </summary>
    public async Task<(int Included, int Excluded)> ReconcileRootAsync(
        WatchedRoot root,
        EffectiveFilter effective,
        CancellationToken ct)
    {
        if (!root.IsActive)
        {
            return (0, await ExcludeAllUnderAsync(root, ct));
        }

        var files = FilesUnder(root);

        // The type half, decided per row from the column the reconciler can read on its own.
        // Everything the allow-list admits has its type cause cleared; the rest gets it set.
        var typeAllowed = effective.AllowedExtensions.Count == 0
            ? files
            : files.Where(f => effective.AllowedExtensions.Contains(f.Extension));

        // Split by the cause we may NOT touch, so the count we report is the truth: a row the scan
        // skipped stays out however wide the allow-list gets, and calling it "included" on the
        // Setup screen would be a number that lies.
        var included = await typeAllowed.Where(f => !f.ExcludedByScan)
            .ExecuteUpdateAsync(SettingsCauses(excludedByType: false, included: true), ct);
        var stillSkipped = await typeAllowed.Where(f => f.ExcludedByScan)
            .ExecuteUpdateAsync(SettingsCauses(excludedByType: false, included: false), ct);

        var wrongType = 0;
        if (effective.AllowedExtensions.Count != 0)
        {
            wrongType = await files.Where(f => !effective.AllowedExtensions.Contains(f.Extension))
                .ExecuteUpdateAsync(SettingsCauses(excludedByType: true, included: false), ct);
        }

        return (included, stillSkipped + wrongType);
    }

    /// <summary>
    /// Marks every file under <paramref name="root"/> as outside the perimeter — the root was
    /// switched off, or removed altogether. <c>ExcludedByRoot</c> is the cause, and it is the one
    /// that comes undone the moment the root is active again, with no scan.
    /// </summary>
    public Task<int> ExcludeAllUnderAsync(WatchedRoot root, CancellationToken ct) =>
        FilesUnder(root).ExecuteUpdateAsync(
            s => s
                .SetProperty(f => f.ExcludedByRoot, true)
                .SetProperty(f => f.IsIncluded, false),
            ct);

    /// <summary>
    /// The two causes reconciliation owns, written together with the <c>IsIncluded</c> they imply.
    /// <c>ExcludedByRoot</c> is always cleared here because every path that reaches it belongs to
    /// an ACTIVE root; <c>ExcludedByScan</c> is never named, which is the whole point.
    /// </summary>
    private static Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<FileEntry>> SettingsCauses(
        bool excludedByType, bool included) =>
        s => s
            .SetProperty(f => f.ExcludedByType, excludedByType)
            .SetProperty(f => f.ExcludedByRoot, false)
            .SetProperty(f => f.IsIncluded, included);

    private IQueryable<FileEntry> FilesUnder(WatchedRoot root)
    {
        var prefix = WatchedRootPath.Normalize(root.RelativePath);
        var query = _db.Files.Where(f => f.VolumeId == root.VolumeId);

        if (prefix.Length == 0)
        {
            return query;
        }

        var prefixWithSep = ScanPath.SubtreePrefix(prefix);
        return query.Where(f =>
            f.Directory.MaterializedPath == prefix ||
            f.Directory.MaterializedPath.StartsWith(prefixWithSep));
    }
}
