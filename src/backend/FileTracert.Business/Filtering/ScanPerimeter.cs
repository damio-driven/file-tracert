using FileTracert.Contracts.Scanning;

namespace FileTracert.Business.Filtering;

/// <summary>
/// Where one scan actually looked: the active watched roots, minus the subtrees the filter threw
/// away, minus the individual files it stepped over inside them.
///
/// <para><b>Why the scan has to hand this to the merge.</b> A row the scan did not touch is
/// missing for one of two reasons that §6 keeps apart: the file is gone from the disk
/// (<c>IsPresent = false</c>), or the scan never looked at it (<c>IsIncluded = false</c>, §4 —
/// a filter decision, reversible without a re-scan). Only the pipeline knows which, and only
/// because it knows where it went; without that, everything that fell out of the perimeter was
/// reported as "no longer on disk", for files that were sitting there the whole time.</para>
///
/// <para>The two questions are asked at different moments and that is why both live here: the
/// governing root is asked once per enumerated item (millions, hence
/// <see cref="RootsBySpecificity"/>), while <see cref="Covers"/> is asked once per catalog
/// directory when the scan closes.</para>
/// </summary>
public sealed class ScanPerimeter
{
    private readonly RootsBySpecificity _roots;
    private readonly ExcludedSubtrees _excluded = new();
    private readonly List<string> _skippedFiles = [];

    /// <param name="normalizedRoots">The ACTIVE watched roots, already normalized
    /// (<see cref="ScanPath.Normalize"/>). An empty set means the scan covered nothing.</param>
    public ScanPerimeter(IEnumerable<string> normalizedRoots) =>
        _roots = RootsBySpecificity.Of(normalizedRoots);

    /// <summary>The most specific active root containing <paramref name="relativePath"/>, or null.</summary>
    public string? GoverningRoot(string relativePath) => _roots.Governing(relativePath);

    /// <summary>Records a directory the filter excluded; its whole subtree goes with it (C16).</summary>
    public void ExcludeSubtree(string relativePath) => _excluded.Add(relativePath);

    /// <summary>
    /// Records a file the scan saw and stepped over for a reason of its own — its attributes, or
    /// an excluded segment in its path. Not the file-TYPE filter: a row that fails THAT is already
    /// <c>IsIncluded = 0</c> in the catalog (<c>FilterReconciler</c> flips it the moment the filter
    /// changes, without a scan), and recording the type-rejected files would mean carrying every
    /// <c>.dll</c> of a watched volume through the merge to say something already said.
    /// </summary>
    public void SkipFile(string relativePath) => _skippedFiles.Add(relativePath);

    public int ExcludedSubtreeCount => _excluded.Count;

    /// <summary>True when the path is, or lives under, a directory the filter excluded.</summary>
    public bool IsExcluded(string relativePath) => _excluded.Covers(relativePath);

    /// <summary>
    /// True when the scan looked at <paramref name="relativePath"/>: inside an active root and not
    /// under an excluded subtree.
    /// </summary>
    public bool Covers(string relativePath) => SkipCause(relativePath) is null;

    /// <summary>
    /// Why the scan did not look at <paramref name="relativePath"/>, or null when it did.
    ///
    /// <para>The two answers are NOT interchangeable and that is the point of step 11h: outside
    /// every active root is a setting the user can flip back, and reconciliation undoes it without
    /// a disk read; rejected by the perimeter rules is a fact about the disk, and only another scan
    /// can retract it. Asking the roots first is also the right precedence — an item outside every
    /// active root was never offered to the filter at all, so it cannot have been "filtered out".
    /// </para>
    /// </summary>
    public ScanSkipCause? SkipCause(string relativePath) =>
        _roots.Governing(relativePath) is null ? ScanSkipCause.InactiveRoot
        : _excluded.Covers(relativePath) ? ScanSkipCause.FilteredOut
        : null;

    /// <summary>
    /// The files skipped one by one, once the subtree exclusions are known. A file under an
    /// excluded subtree is dropped from this list rather than reported twice: its whole directory
    /// is already outside the perimeter, and the directory is one row for the merge instead of one
    /// per file.
    /// <para>Every one of them carries <see cref="ScanSkipCause.FilteredOut"/> by construction:
    /// an item outside every active root is dropped before <see cref="SkipFile"/> is ever reached,
    /// so a file that got this far was offered to the filter and refused by it.</para>
    /// </summary>
    public IReadOnlyList<string> SkippedFiles => _skippedFiles;

    /// <summary>Drops the individually skipped files that an excluded subtree already covers.</summary>
    public int PruneSkippedFilesUnderExcludedSubtrees() =>
        _excluded.Count == 0 ? 0 : _skippedFiles.RemoveAll(_excluded.Covers);
}
