namespace FileTracert.Contracts.Operations;

/// <summary>
/// Thread-safe singleton that tracks planned space reservations and liberations
/// across all queued jobs. Business layer never reads FreeBytesLastKnown directly —
/// the caller (QueueService) fetches the volume row and passes the values in.
///
/// DeltaBytes sign convention (persisted in SpaceLedgerEntries):
///   +bytes → reservation on target (space that will be consumed)
///   −bytes → liberation on source  (space that will be freed after delete)
///
/// Available space for volume V = FreeBytesLastKnown(V) − Σ(active DeltaBytes for V).
/// </summary>
public interface ISpaceLedger
{
    /// <summary>
    /// Pure feasibility check — reads in-memory ledger state, no DB access, no mutation.
    /// The caller provides <paramref name="freeBytesLastKnown"/> and <paramref name="isOnline"/>
    /// from the Volume entity so the ledger stays free of DB dependencies on this hot path.
    ///
    /// Feasibility is FIFO-aware: when evaluating an already-enqueued job, pass
    /// <paramref name="excludeJobId"/> (its own reservation must not count against itself)
    /// and <paramref name="sequenceOrder"/> (only deltas of jobs that PRECEDE it in the
    /// queue apply — a liberation from a job enqueued later cannot be credited).
    /// Pass null/null for a prospective job not yet in the queue (preview, enqueue check):
    /// it would land at the end, so every active delta precedes it.
    /// </summary>
    Task<FeasibilityResult> ComputeFeasibilityAsync(
        int targetVolumeId,
        long freeBytesLastKnown,
        bool isOnline,
        long requiredBytes,
        int? excludeJobId,
        int? sequenceOrder,
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
    /// Deactivates all active ledger entries for a job in DB and memory.
    /// Call on Completed, Failed, or Cancelled.
    /// </summary>
    Task ReleaseAsync(int jobId, CancellationToken ct);

    /// <summary>
    /// Rebuilds in-memory state from all active <c>SpaceLedgerEntries</c> in the DB.
    /// Must be called once at startup before any other operation.
    /// </summary>
    Task RebuildFromDbAsync(CancellationToken ct);
}
