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
///
/// <para>Since step 16 each entry also carries WHICH rules excluded it — all of them, not one,
/// see <see cref="PerimeterVerdict"/> — because the two perimeter rules are inherited differently.
/// A descendant of a path-excluded folder has that segment in its own path, so reconciliation can
/// re-decide it later; a descendant of a HIDDEN folder has nothing of the sort — the exclusion is
/// pure inheritance, and only another scan retracts it.</para>
///
/// <para><b>And that is why the walk unions instead of stopping at the nearest ancestor.</b> The
/// asymmetry above means the deeper verdict is not the more complete one: the path rule re-derives
/// itself at every depth, the attribute rule is recorded only on the folder that actually carries
/// Hidden/System. A hidden folder holding a folder excluded for its path answered with the path
/// cause alone, so the attribute cause disappeared for the whole subtree below — and dropping that
/// segment walked the content of a HIDDEN folder back into the Catalog with no scan. That is the
/// regression 11h exists to prevent, reached one level up. The shipped defaults are exactly this
/// shape: <c>AppData</c> is a seeded excluded segment, <c>%USERPROFILE%\AppData</c> is Hidden, and
/// <c>AppData\Local</c> under it is not.</para>
/// </summary>
public sealed class ExcludedSubtrees
{
    private readonly Dictionary<string, PerimeterVerdict> _roots = new(StringComparer.OrdinalIgnoreCase);

    public ExcludedSubtrees() => Roots = _roots.AsReadOnly();

    public int Count => _roots.Count;

    /// <summary>
    /// Records a directory the filter excluded, and every rule that rejected it. The volume root is
    /// never recorded: excluding it would mean the scan has nothing to do, which is a watched-root
    /// decision, not a filter one.
    ///
    /// <para>A second call for the same path UNIONS instead of replacing. No pipeline reaches that
    /// today — the scan enumerates each directory once and the delta coalesces its records by FRN
    /// before classifying — but "the causes sum" is the invariant this whole type exists to keep,
    /// and last-write-wins is the one spelling of <c>Add</c> that could silently drop one. The union
    /// costs a lookup that was already being paid, so making it unconditional buys the invariant for
    /// nothing rather than leaving it resting on a caller's habit.</para>
    /// </summary>
    public void Add(string relativePath, PerimeterVerdict verdict)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return;
        }

        _roots[relativePath] = _roots.TryGetValue(relativePath, out var already)
            ? already.Union(verdict)
            : verdict;
    }

    /// <summary>
    /// Every excluded directory with the rules that rejected IT — its own verdict, not the union
    /// with its ancestors', which is what <see cref="VerdictFor"/> answers.
    ///
    /// <para>Its own is the right thing for a caller that walks the whole set: an excluded ancestor
    /// is itself an entry here, and whatever it carries it applies to its entire subtree, so the
    /// union is reached by processing the entries rather than by pre-computing it into each one.
    /// <see cref="VerdictFor"/> exists for the opposite shape of question — one path, asked in
    /// isolation, whose ancestors will never be visited.</para>
    ///
    /// <para>A wrapper and not <c>_roots</c> itself: an <see cref="IReadOnlyDictionary{TKey,TValue}"/>
    /// that IS the live <see cref="Dictionary{TKey,TValue}"/> can be cast straight back to one and
    /// written through, which would put a second door on the union <see cref="Add"/> exists to
    /// guarantee. Held in a field rather than built per call because the only caller asks twice per
    /// tick and this type is on the scan's hot path (E7).</para>
    ///
    /// <para><b>It is a live view, and that is a CONSTRAINT on when it may be walked, not a feature
    /// to lean on.</b> The wrapper reads through to the set that is still being filled, so
    /// enumerating it while a <see cref="Add"/> is in flight throws
    /// <see cref="InvalidOperationException"/> — the ordinary dictionary rule, not something this
    /// type softens. Every caller today reads it after the filling is done (the scan closes, the
    /// delta classifies before it reconciles); a caller that wanted to walk it CONCURRENTLY would
    /// need a snapshot of its own, and this type deliberately does not hand one out, because a copy
    /// per call on the scan's hot path is the cost E7 exists to avoid.</para>
    /// </summary>
    public IReadOnlyDictionary<string, PerimeterVerdict> Roots { get; }

    /// <summary>
    /// True when <paramref name="relativePath"/> is, or lives under, an excluded directory. Its own
    /// walk rather than <c>!VerdictFor(...).IsInside</c>, because a yes/no can stop at the first
    /// excluded ancestor while the verdict may not (see below).
    /// </summary>
    public bool Covers(string relativePath)
    {
        var path = relativePath;
        while (_roots.Count > 0 && path.Length > 0)
        {
            if (_roots.ContainsKey(path))
            {
                return true;
            }

            path = ScanPath.Parent(path);
        }

        return false;
    }

    /// <summary>
    /// EVERY rule that excluded <paramref name="relativePath"/> or any of its ancestors — the UNION,
    /// not the nearest match; <see cref="PerimeterVerdict.Inside"/> when nothing on the way up is
    /// excluded.
    ///
    /// <para>The union is the whole point: the causes sum, and the nearest excluded ancestor is not
    /// the most complete answer because the two rules are inherited differently (see the type
    /// remarks). Returning the first hit dropped the attribute cause of a hidden ancestor as soon as
    /// a path-excluded folder sat under it.</para>
    ///
    /// <para>Doing it on the way UP is the honest place: the causes cannot be folded into
    /// <see cref="Add"/> instead, because the MFT dump gives no parent-before-child guarantee — the
    /// ancestor may be recorded after the descendant — which is the same reason C16 collects in
    /// streaming and applies at the end.</para>
    /// </summary>
    public PerimeterVerdict VerdictFor(string relativePath)
    {
        if (_roots.Count == 0)
        {
            return PerimeterVerdict.Inside;
        }

        var accumulated = PerimeterVerdict.Inside;
        var path = relativePath;
        while (path.Length > 0)
        {
            if (_roots.TryGetValue(path, out var verdict))
            {
                accumulated = accumulated.Union(verdict);

                // Nothing an ancestor could add: this set only ever holds FILTER verdicts, so these
                // two are all of it. (InactiveRoot is the roots' answer and never lands here.)
                if (accumulated.ExcludedByPath && accumulated.ExcludedByAttributes)
                {
                    return accumulated;
                }
            }

            path = ScanPath.Parent(path);
        }

        return accumulated;
    }
}
