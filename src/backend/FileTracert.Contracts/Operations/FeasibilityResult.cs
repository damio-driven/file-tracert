namespace FileTracert.Contracts.Operations;

/// <summary>
/// Snapshot of the space-ledger feasibility check for one potential job.
/// Returned by preview and included in the job list DTO for Blocked jobs.
/// </summary>
/// <param name="RequiredBytes">Bytes the job needs on the target volume.</param>
/// <param name="ReservedBytes">Sum of active positive reservations already on the target volume.</param>
/// <param name="AvailableEstimateBytes">Estimated bytes plannable: free − net ledger delta (clamped ≥ 0).</param>
/// <param name="DeficitBytes">How many bytes are still missing (0 when feasible).</param>
/// <param name="EstimateIsLive">True when the target volume is currently online (figures are fresh).</param>
/// <param name="BlockingVolumeId">The volume that caused infeasibility; null when feasible.</param>
/// <param name="Feasible">True when <see cref="AvailableEstimateBytes"/> ≥ <see cref="RequiredBytes"/>.</param>
public sealed record FeasibilityResult(
    long RequiredBytes,
    long ReservedBytes,
    long AvailableEstimateBytes,
    long DeficitBytes,
    bool EstimateIsLive,
    int? BlockingVolumeId,
    bool Feasible);
