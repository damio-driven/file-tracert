using FileTracert.Contracts.Scanning;

namespace FileTracert.Business.Filtering;

/// <summary>
/// The directory subtrees a scan's filter threw away, and the one question the pipeline asks of
/// them: "is this path inside one of them?" (C16).
///
/// <para>NTFS does not propagate Hidden/System to children, so a file inside a hidden folder has
/// perfectly clean attributes of its own. Judging every item only by its OWN attributes let the
/// whole content of an excluded folder through — and the ancestor walk that builds the directory
/// tree then resurrected the excluded folder as a materialized row. Exclusion has to be
/// inherited.</para>
///
/// <para><b>Why a set and an ancestor walk, not a loop over
/// <see cref="ScanPath.IsWithin"/>.</b> The answer is exactly
/// <c>roots.Any(r =&gt; ScanPath.IsWithin(path, r))</c> — same case-insensitive, segment-aware
/// semantics, because walking with <see cref="ScanPath.Parent"/> only ever cuts at a separator.
/// But a volume can have very many excluded folders and millions of items, and the loop is
/// O(items × roots) while the walk is O(items × depth). This is not a sixth subtree matcher (K5):
/// it is set membership over the same predicate, and it is used nowhere but here.</para>
///
/// <para>The set holds EVERY excluded directory, not a minimal set of roots: on a system volume
/// each directory under an excluded segment (<c>Windows</c>, <c>Program Files</c>, …) fails the
/// filter on its own and lands here, which is one string per directory. Pruning descendants in
/// <see cref="Add"/> would not be sound — the USN dump gives no parent-before-child guarantee, so
/// the ancestor may arrive last — and the scan already holds every enumerated item in memory
/// anyway, so this is a fraction of a cost that is already paid.</para>
/// </summary>
/// <para>Since step 16 each entry also carries WHY it was excluded, because the two perimeter
/// rules are inherited differently. A descendant of a path-excluded folder has that segment in its
/// own path, so reconciliation can re-decide it later; a descendant of a HIDDEN folder has nothing
/// of the sort — the exclusion is pure inheritance, and only another scan retracts it. The nearest
/// excluded ancestor wins, which is the most specific answer and the one the walk finds first.</para>
public sealed class ExcludedSubtrees
{
    private readonly Dictionary<string, ScanSkipCause> _roots = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _roots.Count;

    /// <summary>
    /// Records a directory the filter excluded, and the rule that rejected it. The volume root is
    /// never recorded: excluding it would mean the scan has nothing to do, which is a watched-root
    /// decision, not a filter one.
    /// </summary>
    public void Add(string relativePath, ScanSkipCause cause)
    {
        if (!string.IsNullOrEmpty(relativePath))
        {
            _roots[relativePath] = cause;
        }
    }

    /// <summary>True when <paramref name="relativePath"/> is, or lives under, an excluded directory.</summary>
    public bool Covers(string relativePath) => CauseFor(relativePath) is not null;

    /// <summary>
    /// The rule that excluded the nearest excluded ancestor of <paramref name="relativePath"/> (or
    /// the path itself), or null when none of them is excluded.
    /// </summary>
    public ScanSkipCause? CauseFor(string relativePath)
    {
        if (_roots.Count == 0)
        {
            return null;
        }

        var path = relativePath;
        while (path.Length > 0)
        {
            if (_roots.TryGetValue(path, out var cause))
            {
                return cause;
            }

            path = ScanPath.Parent(path);
        }

        return null;
    }
}
