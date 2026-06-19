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

    /// <summary>Cancels a non-terminal job and releases its ledger entries.</summary>
    Task CancelAsync(int jobId, CancellationToken ct);

    /// <summary>Returns all jobs ordered by SequenceOrder. Feasibility is attached for Blocked jobs.</summary>
    Task<PagedResult<OperationJobDto>> ListAsync(int skip, int take, CancellationToken ct);
}
