namespace FileTracert.Contracts.Scanning;

/// <summary>
/// Why a scan did not look at something — the half of the exclusion story only the pipeline knows,
/// carried from the perimeter to the write side so the closing pass can record it on the row
/// (step 11h).
///
/// <para>The fourth cause a row can carry, <c>ExcludedByType</c>, never travels this way: the
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
    /// A segment of the path is on the excluded list (<c>Windows</c>, <c>AppData</c>, …), either
    /// its own or one it inherits from a folder above it. Recorded as <c>Files.ExcludedByPath</c>,
    /// and undone by reconciliation the moment the segment is dropped: every descendant carries
    /// that segment in its own path, so the reconciler can re-decide it from the catalog without
    /// reading a byte of disk (step 16).
    /// </summary>
    ExcludedPath,

    /// <summary>
    /// Its ATTRIBUTES rejected it — Hidden/System, its own or a folder's above it. Recorded as
    /// <c>Files.ExcludedByScan</c>, and undone by NOTHING but another scan: no setting says whether
    /// that folder is still hidden, and a descendant carries nothing in its own path that would
    /// say so either. Pure inheritance, retractable only by looking again.
    /// </summary>
    ExcludedAttributes,
}
