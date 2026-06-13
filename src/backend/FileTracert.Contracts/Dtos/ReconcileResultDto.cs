namespace FileTracert.Contracts.Dtos;

/// <summary>
/// Outcome of reconciling existing index rows to a changed filter (no rescan).
/// <c>NeedsScan</c> = the filter was widened, so new file types may exist on disk
/// but were never indexed — a scan is required to pick them up.
/// </summary>
public sealed record ReconcileResultDto(int IncludedCount, int ExcludedCount, bool NeedsScan);
