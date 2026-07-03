using FileTracert.Contracts.Paging;

namespace FileTracert.Contracts.Operations;

public interface IQueueService
{
    /// <summary>
    /// Creates and enqueues a new job. Reserves space in the ledger for cross-volume moves.
    /// Throws <see cref="EntityAlreadyPendingException"/> if the source entity already has
    /// a non-terminal job (MVP guard: one pending op per entity).
    /// </summary>
    Task<OperationJobDto> EnqueueAsync(CreateJobRequest request, CancellationToken ct);

    /// <summary>
    /// Computes feasibility for the requested operation without creating any job or DB record.
    /// Safe to call from the UI "confirm before enqueue" flow.
    /// </summary>
    Task<FeasibilityResult> PreviewAsync(CreateJobRequest request, CancellationToken ct);

    /// <summary>
    /// Computes feasibility for a whole batch of operations as ONE aggregated demand:
    /// required bytes are summed per target volume and evaluated against the ledger once,
    /// matching how the batch will weigh on the queue when enqueued. Returns the result
    /// of the tightest volume (largest deficit / smallest margin) so the UI never shows
    /// a green light computed on a single file of the batch.
    /// </summary>
    Task<FeasibilityResult> PreviewBatchAsync(IReadOnlyList<CreateJobRequest> requests, CancellationToken ct);

    /// <summary>Cancels a non-terminal job and releases its ledger entries.</summary>
    Task CancelAsync(int jobId, CancellationToken ct);

    /// <summary>
    /// Puts a Blocked or Failed job back in queue (Pending) for another attempt: cleans
    /// leftover <c>.fadit-partial</c> files, resets non-finalized items for a re-copy from
    /// scratch and normalizes the ledger reservation. Rejects any other state
    /// (Completed/Cancelled are terminal by user intent; runnable states are already queued).
    /// </summary>
    Task<OperationJobDto> RetryAsync(int jobId, CancellationToken ct);

    /// <summary>Returns all jobs ordered by SequenceOrder. Feasibility is attached for Blocked jobs.</summary>
    Task<PagedResult<OperationJobDto>> ListAsync(int skip, int take, CancellationToken ct);
}
