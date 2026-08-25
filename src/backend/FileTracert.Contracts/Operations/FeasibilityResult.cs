namespace FileTracert.Contracts.Operations;

/// <summary>
/// Snapshot of the space-ledger feasibility check for one potential job.
/// Returned by preview and included in the job list DTO for Blocked jobs.
/// </summary>
/// <param name="RequiredBytes">Bytes the job needs on the target volume.</param>
/// <param name="ReservedBytes">Sum of active positive reservations already on the target volume.</param>
/// <param name="AvailableEstimateBytes">Estimated bytes plannable: free − net ledger delta (clamped ≥ 0).</param>
/// <param name="DeficitBytes">How many bytes are still missing (0 when feasible).</param>
/// <param name="EstimateIsLive">
/// True when the figures were read from the <b>device</b> in this very check. It is not
/// "the catalog believes the volume is online": since step 11b <c>SpaceCheck</c> probes, and a
/// volume that is flagged online but does not answer falls back to <c>FreeBytesLastKnown</c>
/// with this flag <b>false</b>. False also covers a job that needed no space at all — there is
/// no measurement behind a figure nobody took.
/// </param>
/// <param name="BlockingVolumeId">The volume that caused infeasibility; null when feasible.</param>
/// <param name="Feasible">
/// True when <see cref="AvailableEstimateBytes"/> ≥ <see cref="RequiredBytes"/> +
/// <see cref="MarginBytes"/>.
/// </param>
/// <param name="MarginBytes">
/// Safety cushion demanded on top of <see cref="RequiredBytes"/> (§4: "free space + margin"),
/// derived from <c>AppSettings.SpaceMarginPercent</c>. Reported separately so the required
/// figure stays the honest size of the operation and the UI can explain the difference.
/// </param>
public sealed record FeasibilityResult(
    long RequiredBytes,
    long ReservedBytes,
    long AvailableEstimateBytes,
    long DeficitBytes,
    bool EstimateIsLive,
    int? BlockingVolumeId,
    bool Feasible,
    long MarginBytes = 0);
