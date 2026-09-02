using System.Linq.Expressions;
using FileTracert.Business.Filtering;
using FileTracert.Business.Scanning;
using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Setup;

/// <summary>
/// Reconciles the existing index to a changed filter or perimeter <em>without</em> a rescan or any
/// delete (CLAUDE.md §4) — and, since step 11h, only for the causes it is entitled to undo.
///
/// <para>A row records WHY it is out: <c>ExcludedByType</c> (the extension is off the allow-list),
/// <c>ExcludedByRoot</c> (no active watched root governs it), <c>ExcludedByPath</c> (a segment of
/// its path is on the excluded list) or <c>ExcludedByScan</c> (the scan stepped over it for its
/// ATTRIBUTES — a hidden folder). The first three are facts of the settings and are recomputed
/// here from scratch; the fourth is a fact of the disk, and nothing short of another scan may
/// retract it. Before that distinction existed, widening the type filter re-included the content
/// of a folder the user had hidden, until the next scan pushed it back out.</para>
///
/// <para><b>Step 16 moved the path half over the line.</b> It used to live in <c>ExcludedByScan</c>
/// with the attributes, and this class deliberately never names that column — so ADDING a segment
/// to <c>ExcludedPaths</c> excluded nothing that was already in the catalog, and the rows under it
/// stayed navigable and findable until some full scan happened to pass. It is decidable here
/// because <c>Directories.MaterializedPath</c> is right there: no disk read, which is precisely
/// what separates the causes this class owns from the one it does not.</para>
///
/// <para>Widening cannot resurrect never-indexed rows, so the result still flags
/// <c>NeedsScan</c>. A NARROWING no longer needs one: it is applied here, in full.</para>
/// </summary>
public sealed class FilterReconciler
{
    private readonly FileTracertDbContext _db;
    private readonly IFileSearchIndex _searchIndex;

    public FilterReconciler(FileTracertDbContext db, IFileSearchIndex searchIndex)
    {
        _db = db;
        _searchIndex = searchIndex;
    }

    /// <summary>
    /// True when <paramref name="next"/> lets through something <paramref name="previous"/> did
    /// not: an extension it now allows (empty allow-list = "all", the widest), or a path segment
    /// it no longer excludes. Narrowing on both counts → false.
    ///
    /// <para>The answer means "a scan is needed": widening cannot resurrect what was never
    /// indexed. Dropping an excluded path segment is a widening for exactly that reason — the
    /// rows under it were never written — and reading only the allow-list said "no scan needed"
    /// while nothing on disk had been looked at.</para>
    ///
    /// <para>The converse is now honest too, which it was not before step 16: false for a NARROWING
    /// means "nothing left to do", because <see cref="ReconcileRootAsync"/> applies both halves of
    /// a narrowing to the rows already in the catalog. Until then, adding an excluded segment
    /// returned false while excluding nothing at all.</para>
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

        // Compared the way the segments are MATCHED (FileFilter.SegmentComparer, ASCII-only), not
        // with OrdinalIgnoreCase. Folding wider here than either matching half does answers "nothing
        // was relaxed" for a change both halves treat as two different segments — swapping Über for
        // über re-includes the rows under the first and leaves the never-indexed rows under the
        // second to a scan that this method just said was unnecessary.
        var kept = new HashSet<string>(next.ExcludedPathSegments, FileFilter.SegmentComparer);
        return previous.ExcludedPathSegments.Any(segment => !kept.Contains(segment));
    }

    /// <summary>
    /// Recomputes inclusion for every file under <paramref name="root"/> against the resolved
    /// <paramref name="effective"/> filter and the root's own <see cref="WatchedRoot.IsActive"/>,
    /// leaving <c>ExcludedByScan</c> exactly as the last scan left it. Returns the new
    /// included/excluded counts. Caller owns the transaction.
    ///
    /// <para>An inactive root is handled here rather than by the caller: it is the same question
    /// ("which of the settings-borne causes apply to these rows?"), and answering it in two
    /// places is how the global filter change used to quietly re-include the content of a root the
    /// user had switched off.</para>
    ///
    /// <para><b>The shape of the pass.</b> Rows are partitioned so that every cause is a CONSTANT
    /// inside each statement — which is what lets each one be a single set-based
    /// <c>ExecuteUpdate</c> rather than a computed SET clause per row. Three statements when no
    /// segment is excluded (unchanged from step 11h), five when some are; never one per segment,
    /// and never one per row. This runs inside the Setup transaction, which holds SQLite's only
    /// write lock.</para>
    ///
    /// <para><b>What the path half costs, measured rather than assumed</b> (throwaway probe,
    /// 200 000 files over 2 001 directories, in-memory SQLite, three runs each). End to end the
    /// pass is 2 131–2 162 ms with no excluded segment and 1 972–2 266 ms with five: the same
    /// number, because the extra statements do not write extra rows — they redistribute the same
    /// rows across more of them. Isolated, the framed <c>LIKE</c> evaluated over all 200 000 rows
    /// costs 78–90 ms for one segment and 262–277 ms for five, against 555–638 ms for one flag pass
    /// that writes every row and 1 402–1 526 ms for the FTS resync, which is 70% of the whole thing
    /// and predates step 16. Writing rows dominates; the predicate does not.</para>
    ///
    /// <para>So the per-directory form (11h/E4's idiom — resolve the directories under the segment,
    /// then name the files by <c>DirectoryId</c>) was NOT taken: at this scale it would trade
    /// ~10% of the pass for a second shape of the same rule, and it would need a clause of its own
    /// for the case the frame gets for free — a FILE whose own name is the segment, which
    /// <see cref="FileFilter.IsPathExcluded"/> matches and which the two halves must agree on. The
    /// numbers are here so whoever revisits it starts from them.</para>
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
        var underSegment = UnderExcludedSegment(effective);

        // The path half, decided per row from MaterializedPath — no disk read, which is what makes
        // it this class's business at all. With no excluded segments there is nothing to split on,
        // and the pass keeps the exact shape (and cost) it had before step 16.
        var insidePath = underSegment is null ? files : files.Where(Negate(underSegment));

        var (typeAllowed, wrongType) = SplitByType(insidePath, effective);

        // Split by the cause we may NOT touch, so the count we report is the truth: a row the scan
        // skipped stays out however wide the allow-list gets, and calling it "included" on the
        // Setup screen would be a number that lies.
        var included = await typeAllowed.Where(f => !f.ExcludedByScan)
            .ExecuteUpdateAsync(SettingsCauses(byType: false, byPath: false, included: true), ct);
        var excluded = await typeAllowed.Where(f => f.ExcludedByScan)
            .ExecuteUpdateAsync(SettingsCauses(byType: false, byPath: false, included: false), ct);

        if (wrongType is not null)
        {
            excluded += await wrongType
                .ExecuteUpdateAsync(SettingsCauses(byType: true, byPath: false, included: false), ct);
        }

        if (underSegment is not null)
        {
            // Path-excluded rows are never included, whatever the other causes say — so the only
            // reason to split them again is to write the TYPE verdict truthfully. A `.tmp` under
            // AppData is out twice over, and undoing one cause must not be enough (step 11h).
            var (pathAllowed, pathWrongType) = SplitByType(files.Where(underSegment), effective);

            excluded += await pathAllowed
                .ExecuteUpdateAsync(SettingsCauses(byType: false, byPath: true, included: false), ct);

            if (pathWrongType is not null)
            {
                excluded += await pathWrongType
                    .ExecuteUpdateAsync(SettingsCauses(byType: true, byPath: true, included: false), ct);
            }
        }

        await SyncSearchIndexAsync(root, ct);
        return (included, excluded);
    }

    /// <summary>
    /// Splits a row set by the extension allow-list. The rejected half is null when the allow-list
    /// is empty ("every type"), because then there is no such row and no statement to spend.
    /// </summary>
    private static (IQueryable<FileEntry> Allowed, IQueryable<FileEntry>? Rejected) SplitByType(
        IQueryable<FileEntry> files, EffectiveFilter effective) =>
        effective.AllowedExtensions.Count == 0
            ? (files, null)
            : (files.Where(f => effective.AllowedExtensions.Contains(f.Extension)),
               files.Where(f => !effective.AllowedExtensions.Contains(f.Extension)));

    /// <summary>
    /// Marks every file under <paramref name="root"/> as outside the perimeter — the root was
    /// switched off, or removed altogether. <c>ExcludedByRoot</c> is the cause, and it is the one
    /// that comes undone the moment the root is active again, with no scan.
    /// </summary>
    public async Task<int> ExcludeAllUnderAsync(WatchedRoot root, CancellationToken ct)
    {
        var excluded = await FilesUnder(root).ExecuteUpdateAsync(
            s => s
                .SetProperty(f => f.ExcludedByRoot, true)
                .SetProperty(f => f.IsIncluded, false),
            ct);

        await SyncSearchIndexAsync(root, ct);
        return excluded;
    }

    /// <summary>
    /// The three causes reconciliation owns, written together with the <c>IsIncluded</c> they
    /// imply. <c>ExcludedByRoot</c> is always cleared here because every path that reaches it
    /// belongs to an ACTIVE root; <c>ExcludedByScan</c> is never named, which is the whole point.
    /// </summary>
    private static Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<FileEntry>> SettingsCauses(
        bool byType, bool byPath, bool included) =>
        s => s
            .SetProperty(f => f.ExcludedByType, byType)
            .SetProperty(f => f.ExcludedByRoot, false)
            .SetProperty(f => f.ExcludedByPath, byPath)
            .SetProperty(f => f.IsIncluded, included);

    // ── the excluded-segment predicate ────────────────────────────────────────

    /// <summary>
    /// The path separator, from the shared kernel rather than re-spelled here: it is the same one
    /// every volume-relative path is normalized to, and the frame below is only correct because
    /// <c>MaterializedPath</c> uses it.
    /// </summary>
    private const string Separator = ScanPath.Separator;

    /// <summary>
    /// The LIKE escape character, and it CANNOT be the backslash the rest of this file uses.
    /// A backslash is the path separator we frame with, so with <c>ESCAPE '\'</c> the pattern's
    /// own <c>\%</c> tail would stop being a wildcard and start being a literal percent sign —
    /// the pattern would match nothing and the exclusion would silently apply to no row.
    /// </summary>
    private const string LikeEscape = "!";

    /// <summary>
    /// "Does a segment of this file's volume-relative path sit on the excluded list?", in SQL, as
    /// ONE predicate however many segments there are — null when the list is empty.
    ///
    /// <para><b>The frame is what makes it one case instead of four.</b> The row's path and NAME
    /// are wrapped in separators (<c>\dir\sub\file.jpg\</c>) and the segment is wrapped too
    /// (<c>%\AppData\%</c>), so first segment, last segment, middle segment and whole-path all
    /// collapse into a single <c>LIKE</c>. The NAME is part of the frame because
    /// <see cref="FileFilter.IsPathExcluded"/> splits the file's RELATIVE path, which includes it —
    /// matching the scan's semantics exactly is the requirement, not a choice: a scan and a
    /// reconciliation that disagree would give the catalog two different answers about one file.
    /// </para>
    ///
    /// <para><b>Case folding</b> is SQLite's <c>LIKE</c>, which folds ASCII only — the same
    /// limitation as <c>NOCASE</c> on <c>MaterializedPath</c> (step 9a/P2), and the reason
    /// <see cref="FileFilter.IsPathExcluded"/> folds ASCII-only too rather than with
    /// <c>OrdinalIgnoreCase</c>. A non-ASCII case variant is a miss here exactly as it is there,
    /// and consistently so.</para>
    ///
    /// <para><b>Provider-specific semantics living in Business, declared</b> (§3 wants this layer
    /// provider-agnostic). Three things here are facts about SQLite and not about SQL: the framing
    /// works because <c>LIKE</c>'s <c>%</c> and <c>_</c> are the only metacharacters; the escape
    /// character has to be given explicitly and cannot be the backslash (see
    /// <see cref="LikeEscape"/>); and the ASCII-only fold above is SQLite's, not the standard's.
    /// This class was already written this way before step 16, so nothing regressed — but on SQL
    /// Server the same <c>LIKE</c> folds by the COLUMN's collation, which is usually
    /// case-insensitive and accent-insensitive, and the in-memory half would then be the NARROWER
    /// of the two. The divergence would be silent and in the dangerous direction (reconciliation
    /// re-including rows a scan excluded), so a port has to bring this predicate with it rather
    /// than assume it travels.</para>
    /// </summary>
    private static Expression<Func<FileEntry, bool>>? UnderExcludedSegment(EffectiveFilter effective)
    {
        Expression<Func<FileEntry, bool>>? combined = null;

        foreach (var segment in effective.ExcludedPathSegments)
        {
            // Already normalized and non-empty: EffectiveFilter does that once, for both halves,
            // because the two used to disagree — see EffectiveFilter.ExcludedPathSegments.
            // Escaped, because a '%' or '_' in a configured segment is a character of a folder
            // name and not a wildcard the user asked for.
            var pattern = $"%{Separator}{EscapeLike(segment)}{Separator}%";
            Expression<Func<FileEntry, bool>> one = f => EF.Functions.Like(
                Separator + f.Directory.MaterializedPath + Separator + f.Name + Separator,
                pattern,
                LikeEscape);

            combined = combined is null ? one : Or(combined, one);
        }

        return combined;
    }

    /// <summary>
    /// Escapes the <c>LIKE</c> metacharacters plus <see cref="LikeEscape"/> itself. Deliberately
    /// not shared with <c>SqliteLogStore</c>'s copy: that one escapes for <c>ESCAPE '\'</c>, and
    /// the escape character is the one thing the two cannot agree on here (see
    /// <see cref="LikeEscape"/>).
    /// </summary>
    private static string EscapeLike(string value) => value
        .Replace(LikeEscape, LikeEscape + LikeEscape)
        .Replace("%", LikeEscape + "%")
        .Replace("_", LikeEscape + "_");

    private static Expression<Func<FileEntry, bool>> Or(
        Expression<Func<FileEntry, bool>> left, Expression<Func<FileEntry, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rebound = new ParameterSwap(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<FileEntry, bool>>(Expression.OrElse(left.Body, rebound), parameter);
    }

    private static Expression<Func<FileEntry, bool>> Negate(Expression<Func<FileEntry, bool>> predicate) =>
        Expression.Lambda<Func<FileEntry, bool>>(Expression.Not(predicate.Body), predicate.Parameters[0]);

    /// <summary>Rebinds a second lambda's parameter onto the first's, so the two bodies compose.</summary>
    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }

    /// <summary>
    /// Brings the FTS5 index back in step with the flags just written — the gap step 11g left open:
    /// the Catalog reads <c>IsIncluded</c> and recovers on its own, while Search reads the index,
    /// which a scan closure had already pruned. Re-widening a filter therefore produced files you
    /// could navigate to and could not find.
    ///
    /// <para>Expressed by DIRECTORY, through the existing set-based
    /// <see cref="IFileSearchIndex.SyncDirectoriesAsync"/>: the row set never leaves the database,
    /// so a root holding a million rows costs the same statements as one holding a hundred. What
    /// crosses the boundary is one int per directory — the reason that method exists.</para>
    /// </summary>
    private async Task SyncSearchIndexAsync(WatchedRoot root, CancellationToken ct)
    {
        var directoryIds = await DirectoriesUnder(root).Select(d => d.Id).ToListAsync(ct);
        await _searchIndex.SyncDirectoriesAsync(directoryIds, ct);
    }

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

    /// <summary>
    /// The directory rows the same subtree covers. Spelled through <see cref="DirectoryQueries"/>
    /// so the two halves cannot drift; the volume-root case is the one that has to be said here,
    /// because an empty path is not a prefix of anything.
    /// </summary>
    private IQueryable<DirectoryNode> DirectoriesUnder(WatchedRoot root)
    {
        var prefix = WatchedRootPath.Normalize(root.RelativePath);
        return prefix.Length == 0
            ? _db.Directories.Where(d => d.VolumeId == root.VolumeId)
            : _db.Directories.InSubtree(root.VolumeId, prefix);
    }
}
