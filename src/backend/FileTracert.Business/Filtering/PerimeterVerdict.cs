using FileTracert.Contracts.Scanning;

namespace FileTracert.Business.Filtering;

/// <summary>
/// WHICH perimeter rules rejected an item — a SET of causes, never a choice between them.
///
/// <para><b>Why a set.</b> The causes of step 11h are flags precisely because they SUM: a row can be
/// out for more than one reason, and each has to be switchable off by its own owner. Answering with
/// a single cause forces a precedence, and any precedence is wrong in the same way — undoing the
/// winner re-admits a row the loser should still be holding out. Concretely: a HIDDEN folder that
/// also sits under an excluded path segment would record only the segment, and dropping that segment
/// would walk the folder's content back into the Catalog with no scan. That is the regression 11h
/// exists to prevent, reached through a new door, and it would be history-dependent on top — a scan
/// that had seen the folder hidden before the segment was excluded leaves the row protected, the
/// other order leaves it exposed.</para>
///
/// <para>Recording every cause costs nothing downstream: the closing pass runs one statement per
/// DISTINCT cause (<c>BulkIndexWriter.ExcludeForCauseAsync</c>), not one per area, so a directory
/// rejected by both rules simply contributes to two statements that were going to run anyway.</para>
///
/// <para>Callers that only need "inside or outside?" ask <see cref="IsInside"/>; callers that write
/// the row enumerate, which is why this type is <c>foreach</c>-able over
/// <see cref="ScanSkipCause"/> through a struct enumerator — the scan asks it once per catalog
/// directory when it closes, and this codebase does not hand that path an allocation (E7).</para>
/// </summary>
/// <param name="InactiveRoot">No ACTIVE watched root governs the path. Only
/// <c>ScanPerimeter</c> can raise this: the filter is never even offered such an item.</param>
/// <param name="ExcludedByPath">A segment of the path is on the excluded list.</param>
/// <param name="ExcludedByAttributes">Hidden/System rejected it.</param>
public readonly record struct PerimeterVerdict(
    bool InactiveRoot,
    bool ExcludedByPath,
    bool ExcludedByAttributes)
{
    /// <summary>Nothing rejected it.</summary>
    public static PerimeterVerdict Inside => default;

    /// <summary>Outside every active root — the one verdict the roots, not the filter, produce.</summary>
    public static PerimeterVerdict OutsideEveryRoot { get; } =
        new(InactiveRoot: true, ExcludedByPath: false, ExcludedByAttributes: false);

    public bool IsInside => !InactiveRoot && !ExcludedByPath && !ExcludedByAttributes;

    /// <summary>
    /// Every cause of both verdicts. The operation the "causes sum" rule needs whenever two
    /// verdicts apply to the same row — which happens as soon as one excluded directory sits under
    /// another (see <c>ExcludedSubtrees.VerdictFor</c>): taking either one alone drops a cause, and
    /// a dropped cause is a row that comes back the moment its surviving cause is undone.
    /// </summary>
    public PerimeterVerdict Union(PerimeterVerdict other) => new(
        InactiveRoot || other.InactiveRoot,
        ExcludedByPath || other.ExcludedByPath,
        ExcludedByAttributes || other.ExcludedByAttributes);

    /// <summary>The causes this verdict carries, in no significant order and without allocating.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>A struct enumerator over at most three causes; see the note on allocation above.</summary>
    public struct Enumerator(PerimeterVerdict verdict)
    {
        private int _index = -1;

        public ScanSkipCause Current { get; private set; } = default;

        public bool MoveNext()
        {
            while (++_index < 3)
            {
                switch (_index)
                {
                    case 0 when verdict.InactiveRoot:
                        Current = ScanSkipCause.InactiveRoot;
                        return true;
                    case 1 when verdict.ExcludedByPath:
                        Current = ScanSkipCause.ExcludedPath;
                        return true;
                    case 2 when verdict.ExcludedByAttributes:
                        Current = ScanSkipCause.ExcludedAttributes;
                        return true;
                }
            }

            return false;
        }
    }
}
