using FileTracert.Business.Scanning;

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
/// But a volume can have thousands of excluded folders and millions of items, and the loop is
/// O(items × roots) while the walk is O(items × depth). This is not a sixth subtree matcher (K5):
/// it is set membership over the same predicate, and it is used nowhere but here.</para>
/// </summary>
public sealed class ExcludedSubtrees
{
    private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _roots.Count;

    /// <summary>
    /// Records a directory the filter excluded. The volume root is never recorded: excluding it
    /// would mean the scan has nothing to do, which is a watched-root decision, not a filter one.
    /// </summary>
    public void Add(string relativePath)
    {
        if (!string.IsNullOrEmpty(relativePath))
        {
            _roots.Add(relativePath);
        }
    }

    /// <summary>True when <paramref name="relativePath"/> is, or lives under, an excluded directory.</summary>
    public bool Covers(string relativePath)
    {
        if (_roots.Count == 0)
        {
            return false;
        }

        var path = relativePath;
        while (path.Length > 0)
        {
            if (_roots.Contains(path))
            {
                return true;
            }

            path = ScanPath.Parent(path);
        }

        return false;
    }
}
