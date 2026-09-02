using FileTracert.Contracts.Scanning;

namespace FileTracert.Business.Filtering;

/// <summary>
/// The resolved file-type filter applied during a scan: global defaults from
/// <c>AppSettings</c>, optionally overridden per <c>WatchedRoot</c>.
/// </summary>
/// <param name="AllowedExtensions">
/// Lower-cased extensions (no dot) to include. Empty = allow every extension.
/// </param>
/// <param name="ExcludedPathSegments">
/// Path segments (e.g. <c>Windows</c>, <c>$Recycle.Bin</c>, <c>AppData</c>) that
/// exclude any entry whose relative path contains them. Normalized on the way in — see the
/// property below, which is where that happens and why.
/// </param>
/// <param name="ExcludeSystem">Exclude entries with the System attribute.</param>
/// <param name="ExcludeHidden">Exclude entries with the Hidden attribute.</param>
public sealed record EffectiveFilter(
    IReadOnlySet<string> AllowedExtensions,
    IReadOnlyList<string> ExcludedPathSegments,
    bool ExcludeSystem = true,
    bool ExcludeHidden = true)
{
    /// <summary>
    /// The excluded segments, normalized ONCE, here — not in the builder and not in either
    /// consumer.
    ///
    /// <para><b>Why on the type and not at the call site.</b> Two halves read this list and they
    /// must agree exactly: <see cref="FileFilter.IsPathExcluded"/> when a scan decides an item, and
    /// <c>FilterReconciler</c>'s <c>LIKE</c> when Setup re-decides the rows already in the catalog.
    /// They did not. <c>Windows\</c> — the spelling CLAUDE.md §4 itself uses — normalized on the SQL
    /// side and was compared raw in memory, so a reconciliation excluded the rows and the next scan
    /// put them all back: an exclusion that appears to work and then silently undoes itself, which
    /// is the failure mode step 16 exists to remove, not to introduce. Normalizing in the builder
    /// would have fixed today's callers; normalizing in the value makes the divergence
    /// unconstructible, and the cost is one list per resolved filter — a handful per scan.</para>
    ///
    /// <para><see cref="FilterReconciler.FilterWidened"/> compares these lists, so it gets the same
    /// answer for free: dropping <c>Windows\</c> in favour of <c>Windows</c> is not a widening.
    /// </para>
    ///
    /// <para>Normalizing in the <c>init</c> accessor rather than in a property initializer so that a
    /// <c>with</c> expression cannot slip a raw list past it either — the whole point is that there
    /// is no way to hold an EffectiveFilter whose two readers would disagree.</para>
    /// </summary>
    public IReadOnlyList<string> ExcludedPathSegments
    {
        get => _excludedPathSegments;
        init => _excludedPathSegments = NormalizeSegments(value);
    }

    private readonly IReadOnlyList<string> _excludedPathSegments = NormalizeSegments(ExcludedPathSegments);

    /// <summary>
    /// Trims whitespace (no Windows path component may carry it, and the user who typed it meant
    /// the folder), folds <c>/</c> onto <c>\</c> and strips the outer separators, drops what is left
    /// empty, and de-duplicates case-insensitively so the SQL side does not OR the same term twice.
    ///
    /// <para>What it deliberately does NOT do is reduce a multi-part segment to a single one:
    /// <c>AppData\Local</c> stays as it is and is matched as a SEQUENCE of segments, which is the
    /// semantics the SQL frame already had. The alternative — whole-segment equality — is not a
    /// choice between two meanings, it is a configured value that can never match anything.</para>
    /// </summary>
    private static IReadOnlyList<string> NormalizeSegments(IEnumerable<string> segments) =>
        segments
            .Select(s => ScanPath.Normalize(s.Trim()))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
