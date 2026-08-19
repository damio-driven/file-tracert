using FileTracert.Contracts.Paging;

namespace FileTracert.Contracts.Operations;

public interface IQueueService
{
    /// <summary>
    /// Creates and enqueues a new job. Reserves space in the ledger for cross-volume moves.
    /// Never rejects over a conflict with another queued job (§4 "non rifiutare mai un job
    /// all'enqueue"): when the requested entity — or a folder above or below it — is already
    /// held by a non-terminal job, the new job is created
    /// <see cref="Enums.JobState.Blocked"/> /
    /// <see cref="Enums.JobBlockReason.DependencyPending"/> with
    /// <c>DependsOnJobId</c> naming the job it waits for (MVP: one pending op per entity).
    /// Throws only for requests that are invalid in themselves.
    /// </summary>
    Task<OperationJobDto> EnqueueAsync(CreateJobRequest request, CancellationToken ct);

    /// <summary>
    /// Enqueues a whole selection as ONE unit of work: either every request becomes a job or
    /// none does (C25). A single invalid request aborts the batch and nothing is persisted, so
    /// the caller can fix it and repeat the same gesture without duplicating anything.
    /// Every element still goes through the same per-job path as
    /// <see cref="EnqueueAsync"/> — conflict guard, sequence number, projection overlay, ledger
    /// reservation — and the returned jobs are in request order, each carrying the state it was
    /// born in (a job parked <see cref="Enums.JobBlockReason.DependencyPending"/> included, so
    /// the UI can say how many of them are waiting).
    /// Throws only for requests that are invalid in themselves; the message names the position
    /// of the offending element.
    /// </summary>
    Task<IReadOnlyList<OperationJobDto>> EnqueueBatchAsync(
        IReadOnlyList<CreateJobRequest> requests, CancellationToken ct);

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
