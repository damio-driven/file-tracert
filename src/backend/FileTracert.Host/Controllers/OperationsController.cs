using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Paging;
using FileTracert.Host.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Queue operation endpoints: enqueue, preview, list, retry, cancel.
///
/// <para>Every action's error handling lives in <see cref="QueueExceptionFilter"/> (K11): the same
/// try/catch was copied six times, and twice it chose between 404 and 400 by looking for the words
/// "not found" inside the message. The rule is now carried by the exception TYPE, and — §9 — the
/// filter logs what it converts, which four of the six actions did not do.</para>
/// </summary>
[ApiController]
[Route("api/operations")]
[TypeFilter<QueueExceptionFilter>]
public sealed class OperationsController : ControllerBase
{
    private readonly IQueueService _queue;

    public OperationsController(IQueueService queue) => _queue = queue;

    /// <summary>Returns all jobs ordered by sequence, with feasibility attached for Blocked jobs.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<OperationJobDto>>> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var paged = new PagedRequest(skip, take).Normalized();
        return Ok(await _queue.ListAsync(paged.Skip, paged.Take, ct));
    }

    /// <summary>
    /// Creates and enqueues a new operation. Reserves space for cross-volume moves.
    /// Never refuses over a conflict with another queued job (§4): the job is created
    /// Blocked(DependencyPending) instead, with DependsOnJobId naming what it waits for.
    /// 400 is reserved for requests that are wrong in themselves (bad name, missing volume).
    /// </summary>
    [HttpPost("enqueue")]
    public async Task<ActionResult<OperationJobDto>> Enqueue(
        [FromBody] CreateJobRequest req,
        CancellationToken ct)
    {
        var dto = await _queue.EnqueueAsync(req, ct);
        return CreatedAtAction(nameof(List), new { }, dto);
    }

    /// <summary>
    /// Enqueues a whole selection in one call and one transaction (C25): all the jobs or none.
    /// A request that is invalid in itself aborts the batch with a 400 naming its position —
    /// nothing is persisted, so repeating the corrected gesture cannot duplicate anything.
    /// Returns the created jobs in request order, each with the state it was born in.
    /// </summary>
    [HttpPost("enqueue-batch")]
    public async Task<ActionResult<IReadOnlyList<OperationJobDto>>> EnqueueBatch(
        [FromBody] List<CreateJobRequest> reqs,
        CancellationToken ct)
    {
        var dtos = await _queue.EnqueueBatchAsync(reqs, ct);
        return CreatedAtAction(nameof(List), new { }, dtos);
    }

    /// <summary>
    /// Computes feasibility for an operation without creating any DB record.
    /// Safe to call from the UI "confirm" dialog.
    /// </summary>
    [HttpPost("preview")]
    public async Task<ActionResult<FeasibilityResult>> Preview(
        [FromBody] CreateJobRequest req,
        CancellationToken ct)
        => Ok(await _queue.PreviewAsync(req, ct));

    /// <summary>
    /// Computes feasibility for a whole batch of operations as one aggregated demand
    /// (required bytes summed per target volume). The UI confirm dialog uses this so the
    /// verdict reflects the entire selection, not just the first file.
    /// </summary>
    [HttpPost("preview-batch")]
    public async Task<ActionResult<FeasibilityResult>> PreviewBatch(
        [FromBody] List<CreateJobRequest> reqs,
        CancellationToken ct)
        => Ok(await _queue.PreviewBatchAsync(reqs, ct));

    /// <summary>
    /// Puts a Blocked or Failed job back in queue for another attempt (Riprova).
    /// Returns 400 for non-retryable states (Completed, Cancelled, already queued/running)
    /// and 404 when there is no such job.
    /// </summary>
    [HttpPost("{id:int}/retry")]
    public async Task<ActionResult<OperationJobDto>> Retry(int id, CancellationToken ct)
        => Ok(await _queue.RetryAsync(id, ct));

    /// <summary>Cancels a non-terminal job and releases its ledger reservation.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        await _queue.CancelAsync(id, ct);
        return NoContent();
    }
}
