using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;

namespace FileTracert.Business.Filtering;

/// <summary>
/// Pure, testable inclusion logic applied <em>inside</em> the scan pipeline so the
/// index is born lean (CLAUDE.md §4). No I/O, no DB.
/// </summary>
public static class FileFilter
{
    /// <summary>Extracts the lower-cased extension (no dot) from a file name; empty when none.</summary>
    public static string GetExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
        {
            return string.Empty;
        }

        return name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>Resolves a file category from its extension via the seeded map; unknown → Other.</summary>
    public static FileCategory ResolveCategory(string extension, IReadOnlyDictionary<string, FileCategory> map) =>
        map.TryGetValue(extension, out var category) ? category : FileCategory.Other;

    /// <summary>
    /// True when the relative path goes through one of the excluded segments — the SAME question
    /// <c>FilterReconciler</c> asks in SQL, and it has to be the same question: a scan and a
    /// reconciliation that disagree give the catalog two different answers about one file, and the
    /// one that runs last wins. That is exactly how <c>Windows\</c> used to exclude rows in Setup
    /// and have the next scan put every one of them back.
    ///
    /// <para><b>Framing, not splitting.</b> The segment is matched where it sits between separators
    /// (or at either end of the path), which is the SQL side's <c>%\segment\%</c> against the framed
    /// path spelled with spans. Whole-segment equality on a split would have been the same rule for
    /// a one-part segment and NO rule at all for a multi-part one — <c>AppData\Local</c> could then
    /// never match anything, which is not a semantics, it is a silent misconfiguration.</para>
    ///
    /// <para>Allocation-free, because a scan asks this once per enumerated item — millions on a
    /// real volume (E7). It replaces a <c>Split</c> that allocated an array plus a string per
    /// segment on every one of those calls; the segments arrive already normalized
    /// (<see cref="EffectiveFilter.ExcludedPathSegments"/>), so nothing is built here either.</para>
    ///
    /// <para>Case folding is <c>OrdinalIgnoreCase</c>, matching SQLite's ASCII-only <c>LIKE</c> on
    /// the other side and <c>NOCASE</c> on <c>MaterializedPath</c> (9a/P2) — the known limit, and
    /// the same one in all three places.</para>
    /// </summary>
    public static bool IsPathExcluded(string relativePath, EffectiveFilter filter)
    {
        if (filter.ExcludedPathSegments.Count == 0 || relativePath.Length == 0)
        {
            return false;
        }

        // The pipeline hands out backslash paths; the forward-slash fold is kept for the callers
        // that do not (the check itself is a vectorised scan, so it costs nothing when it fails).
        var normalized = relativePath.Contains('/') ? relativePath.Replace('/', '\\') : relativePath;
        var path = normalized.AsSpan().Trim('\\');

        foreach (var excluded in filter.ExcludedPathSegments)
        {
            if (ContainsFramed(path, excluded))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="segment"/> occurs in <paramref name="path"/> bounded by separators
    /// or by the ends of the path — the in-memory spelling of the SQL frame.
    /// </summary>
    private static bool ContainsFramed(ReadOnlySpan<char> path, string segment)
    {
        var start = 0;
        while (start <= path.Length - segment.Length)
        {
            var found = path[start..].IndexOf(segment, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            var at = start + found;
            var end = at + segment.Length;
            if ((at == 0 || path[at - 1] == '\\') && (end == path.Length || path[end] == '\\'))
            {
                return true;
            }

            start = at + 1;
        }

        return false;
    }

    /// <summary>
    /// The PERIMETER half of the filter, and WHICH of its two rules rejected the item — BOTH of
    /// them when both apply. Never <see cref="PerimeterVerdict.InactiveRoot"/>: the roots are asked
    /// before the filter is, and an item outside every active root is never offered to it.
    ///
    /// <para>Step 16 made the answer a set of causes rather than a bool because the two rules are
    /// undone by different owners, exactly as step 11h found for the three causes it split: a path
    /// segment is a fact of the settings and reconciliation retracts it with no disk read, while an
    /// attribute is a fact of the disk and only another scan can. Folded into one verdict, the first
    /// was hostage to the second — and folded into one CAUSE, with either precedence, undoing the
    /// winner would re-admit a row the loser still holds out. See <see cref="PerimeterVerdict"/>.
    /// </para>
    ///
    /// <para>One <see cref="IsPathExcluded"/> call, not two: it splits the path on every
    /// invocation, and the 11g review had already taken a second split off this branch. The
    /// attribute half is two flag tests, so asking it unconditionally costs nothing measurable.
    /// </para>
    /// </summary>
    public static PerimeterVerdict EvaluatePerimeter(
        string relativePath, FileAttributes attributes, EffectiveFilter filter) =>
        new(InactiveRoot: false,
            ExcludedByPath: IsPathExcluded(relativePath, filter),
            ExcludedByAttributes: IsExcludedByAttributes(attributes, filter));

    /// <summary>
    /// The PERIMETER half of the filter as a yes/no, for the callers that do not need to record
    /// which rules spoke. <see cref="EvaluatePerimeter"/> is the same question with its answer kept.
    /// </summary>
    public static bool IsInsidePerimeter(string relativePath, FileAttributes attributes, EffectiveFilter filter) =>
        EvaluatePerimeter(relativePath, attributes, filter).IsInside;

    /// <summary>
    /// Directories are not filtered by extension (the tree needs them), so the perimeter rules
    /// are all there is to ask.
    /// </summary>
    public static bool ShouldIncludeDirectory(string relativePath, FileAttributes attributes, EffectiveFilter filter) =>
        IsInsidePerimeter(relativePath, attributes, filter);

    /// <summary>
    /// The TYPE half: the extension allow-list, empty meaning "every type". The other half of
    /// <see cref="ShouldIncludeFile"/>, spelled apart because a caller sometimes needs to know
    /// WHICH half rejected a file — the scan does, to tell "outside the perimeter" (recorded on
    /// the row) from "wrong type" (never indexed in the first place).
    /// </summary>
    public static bool IsAllowedType(string extension, EffectiveFilter filter) =>
        filter.AllowedExtensions.Count == 0 || filter.AllowedExtensions.Contains(extension);

    /// <summary>Files honor the perimeter rules plus the extension allow-list.</summary>
    public static bool ShouldIncludeFile(
        string relativePath,
        string extension,
        FileAttributes attributes,
        EffectiveFilter filter) =>
        IsInsidePerimeter(relativePath, attributes, filter) && IsAllowedType(extension, filter);

    private static bool IsExcludedByAttributes(FileAttributes attributes, EffectiveFilter filter) =>
        (filter.ExcludeSystem && attributes.HasFlag(FileAttributes.System)) ||
        (filter.ExcludeHidden && attributes.HasFlag(FileAttributes.Hidden));
}
