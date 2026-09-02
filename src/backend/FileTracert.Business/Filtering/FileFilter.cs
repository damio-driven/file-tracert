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
    /// <para><b>Case folding is ASCII-only</b>, deliberately, because the other half cannot be
    /// anything else: SQLite's <c>LIKE</c> folds <c>a-z</c> and nothing more, exactly like
    /// <c>NOCASE</c> on <c>MaterializedPath</c> (9a/P2) and like the merge's match by path. This
    /// side used to fold with <c>OrdinalIgnoreCase</c>, i.e. the whole invariant table, and the two
    /// then disagreed on every non-ASCII case variant — segment <c>Über</c> against
    /// <c>über\x.jpg</c> matched here and missed in SQL. That is not a missed exclusion but a
    /// silent UNDOING of a scan's verdict, since reconciliation writes <c>ExcludedByPath</c> in both
    /// directions: the row comes back, the next scan pushes it out, for ever.
    /// <b>The limit, stated:</b> a non-ASCII case variant of a configured segment does not match —
    /// here, in SQL, and in the catalog's path collation alike.</para>
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

        // An INDEXED loop, not foreach. ExcludedPathSegments is typed as IReadOnlyList<string>, so
        // foreach goes through IEnumerable<string>.GetEnumerator() and BOXES List<string>.Enumerator
        // — 40 bytes on every call of a method whose doc above promises it builds nothing, i.e. tens
        // of megabytes of garbage per scan of a real catalog. The indexer is an interface call and
        // allocates nothing; there is a test that measures it.
        var segments = filter.ExcludedPathSegments;
        for (var i = 0; i < segments.Count; i++)
        {
            if (ContainsFramed(path, segments[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="segment"/> occurs in <paramref name="path"/> bounded by separators
    /// or by the ends of the path — the in-memory spelling of the SQL frame.
    ///
    /// <para>Candidates are only the positions the frame allows: the start of the path, and every
    /// index just past a separator. That is both cheaper than scanning for the segment anywhere and
    /// the reason the comparison can be a plain span compare with the ASCII fold SQLite's
    /// <c>LIKE</c> uses — see the note on <see cref="IsPathExcluded"/>. No allocation on any path.
    /// </para>
    /// </summary>
    private static bool ContainsFramed(ReadOnlySpan<char> path, string segment)
    {
        var needle = segment.AsSpan();
        var start = 0;

        while (start <= path.Length - needle.Length)
        {
            var end = start + needle.Length;
            if ((end == path.Length || path[end] == '\\')
                && EqualsAsciiIgnoreCase(path.Slice(start, needle.Length), needle))
            {
                return true;
            }

            var separator = path[start..].IndexOf('\\');
            if (separator < 0)
            {
                return false;
            }

            start += separator + 1;
        }

        return false;
    }

    /// <summary>
    /// Ordinal equality with the ASCII-only case fold, which is what SQLite's <c>LIKE</c> and
    /// <c>NOCASE</c> do. <c>OrdinalIgnoreCase</c> would fold more than the SQL half can, and the two
    /// halves disagreeing about one file is the thing this rule exists to prevent.
    /// </summary>
    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i] && ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static char ToLowerAscii(char c) => (uint)(c - 'A') <= 'Z' - 'A' ? (char)(c | 0x20) : c;

    /// <summary>
    /// Two configured segments compared the way they are MATCHED — the ASCII-only fold above, not
    /// <c>OrdinalIgnoreCase</c>.
    ///
    /// <para>It exists for <c>FilterReconciler.FilterWidened</c>, which asks whether a segment
    /// stopped being excluded and therefore whether a scan is owed. Folding wider than the matcher
    /// made that answer wrong in the direction that hides work: replacing <c>Über</c> with
    /// <c>über</c> is, to both matching halves, two different segments — the rows under <c>Über</c>
    /// are re-included by reconciliation and the rows under <c>über</c> were never indexed, which is
    /// the textbook definition of a widening — and <c>OrdinalIgnoreCase</c> answered "nothing was
    /// relaxed", so the screen said no scan was needed.</para>
    ///
    /// <para>Spelled here rather than in the reconciler so the fold has ONE definition: the whole
    /// point of the ASCII restriction is that everybody who compares a segment does it the same way.
    /// The de-duplication in <see cref="EffectiveFilter"/> still folds wider, on purpose and with
    /// its own note — it drops a spelling both halves would then miss identically, which costs their
    /// agreement nothing.</para>
    /// </summary>
    public static IEqualityComparer<string> SegmentComparer { get; } = new AsciiIgnoreCaseComparer();

    private sealed class AsciiIgnoreCaseComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null && x.Length == y.Length && EqualsAsciiIgnoreCase(x, y);

        public int GetHashCode(string obj)
        {
            var hash = new HashCode();
            foreach (var c in obj)
            {
                hash.Add(ToLowerAscii(c));
            }

            return hash.ToHashCode();
        }
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
