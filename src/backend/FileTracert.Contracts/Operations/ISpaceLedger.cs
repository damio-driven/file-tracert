namespace FileTracert.Contracts.Operations;

/// <summary>
/// Thread-safe singleton that tracks planned space reservations and liberations
/// across all queued jobs. It knows nothing about volumes or settings: the caller
/// (Business.SpaceCheck) brings the free bytes — measured on the device, or the volume row's
/// last-known figure when the device does not answer — and the margin to demand on top.
///
/// DeltaBytes sign convention (persisted in SpaceLedgerEntries):
///   +bytes → reservation on target (space that will be consumed)
///   −bytes → liberation on source  (space that will be freed after delete)
///
/// Available space for volume V = free(V) − Σ(active DeltaBytes for V).
/// </summary>
public interface ISpaceLedger
{
    /// <summary>
    /// Pure feasibility check — reads in-memory ledger state, no DB access, no mutation.
    /// The caller provides <paramref name="freeBytes"/> — read from the device when it can be,
    /// from the Volume row when it cannot — and says with <paramref name="estimateIsLive"/> which
    /// of the two it is, so the ledger stays free of DB and platform dependencies on this hot path.
    ///
    /// Feasibility is FIFO-aware: when evaluating an already-enqueued job, pass
    /// <paramref name="excludeJobId"/> (its own reservation must not count against itself)
    /// and <paramref name="sequenceOrder"/> (only deltas of jobs that PRECEDE it in the
    /// queue apply — a liberation from a job enqueued later cannot be credited).
    /// Pass null/null for a prospective job not yet in the queue (preview, enqueue check):
    /// it would land at the end, so every active delta precedes it.
    ///
    /// <paramref name="marginBytes"/> is the §4 safety cushion the caller wants on top of
    /// <paramref name="requiredBytes"/>: the demand the deficit is computed against is the sum of
    /// the two, while the result keeps reporting them apart. The ledger never reads it from the
    /// settings itself — it is a singleton on a hot path, and one DB round-trip per feasibility
    /// question is a round-trip too many.
    ///
    /// <paramref name="includeQueuedLiberations"/> selects the view:
    ///   true  → PLANNING view (enqueue/preview): promised liberations (negative deltas) of
    ///           preceding jobs count as available — the queue will materialize them in order.
    ///   false → HARD view (execution re-check, Blocked revaluation): a liberation is only a
    ///           promise until the freeing job completes; never copy on its strength. Only
    ///           reservations (positive deltas) of preceding jobs are subtracted.
    /// </summary>
    Task<FeasibilityResult> ComputeFeasibilityAsync(
        int targetVolumeId,
        long freeBytes,
        bool estimateIsLive,
        long requiredBytes,
        long marginBytes,
        int? excludeJobId,
        int? sequenceOrder,
        bool includeQueuedLiberations,
        CancellationToken ct);

    /// <summary>
    /// Persists <see cref="SpaceLedgerEntries"/> for a newly-Pending job and
    /// registers them in memory. <paramref name="sequenceOrder"/> is the job's FIFO
    /// position, kept alongside each entry for order-aware feasibility.
    /// For intra-volume ops pass <paramref name="requiredBytes"/> = 0 and no source.
    /// </summary>
    Task ReserveAsync(
        int jobId,
        int sequenceOrder,
        int targetVolumeId,
        long requiredBytes,
        int? sourceVolumeId,
        long freedBytes,
        CancellationToken ct);

    /// <summary>
    /// Registers an already-persisted reservation in the in-memory mirror only (no DB write).
    /// Used by the atomic enqueue path (fix C3): the caller persists the ledger entries inside the
    /// job's own transaction, then calls this AFTER the commit succeeds — so the mirror never
    /// reflects a reservation the database has not durably committed.
    /// </summary>
    Task RegisterReservationInMemoryAsync(
        int jobId,
        int sequenceOrder,
        int targetVolumeId,
        long requiredBytes,
        int? sourceVolumeId,
        long freedBytes,
        CancellationToken ct);

    /// <summary>
    /// Deactivates all active ledger entries for a job in DB and memory.
    /// For NON-terminal normalization only (retry, revaluation). Terminal transitions
    /// (Completed/Failed/Cancelled) must instead deactivate the DB rows inside their own
    /// state-commit transaction (finding #5 — no phantom reservations on crash) and then
    /// call <see cref="ReleaseInMemoryAsync"/> after the commit.
    /// </summary>
    Task ReleaseAsync(int jobId, CancellationToken ct);

    /// <summary>
    /// Removes a job's entries from the in-memory mirror only (no DB write). Counterpart of
    /// <see cref="RegisterReservationInMemoryAsync"/>: the caller has already deactivated the
    /// rows inside the terminal-state transaction and calls this AFTER the commit succeeds.
    /// </summary>
    Task ReleaseInMemoryAsync(int jobId, CancellationToken ct);

    /// <summary>
    /// Rebuilds in-memory state from all active <c>SpaceLedgerEntries</c> in the DB.
    /// Must be called once at startup before any other operation.
    /// </summary>
    Task RebuildFromDbAsync(CancellationToken ct);
}
