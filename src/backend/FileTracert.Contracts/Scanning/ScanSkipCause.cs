namespace FileTracert.Contracts.Scanning;

/// <summary>
/// Why a scan did not look at something — the half of the exclusion story only the pipeline knows,
/// carried from the perimeter to the write side so the closing pass can record it on the row
/// (step 11h).
///
/// <para>The third cause a row can carry, <c>ExcludedByType</c>, never travels this way: the
/// extension is on the row already, so <c>FilterReconciler</c> decides it on its own and a scan has
/// nothing to add. What a scan knows and nobody else does is where it went.</para>
/// </summary>
public enum ScanSkipCause
{
    /// <summary>
    /// No ACTIVE watched root governs the path. Recorded as <c>Files.ExcludedByRoot</c>, and undone
    /// by reconciliation the moment the root is switched back on — no disk read involved.
    /// </summary>
    InactiveRoot,

    /// <summary>
    /// The perimeter half of the filter rejected it: its own attributes (Hidden/System), an
    /// excluded segment in its path, or a folder above it that failed one of those. Recorded as
    /// <c>Files.ExcludedByScan</c>, and undone by NOTHING but another scan — no setting says whether
    /// that folder is still hidden.
    /// </summary>
    FilteredOut,
}
