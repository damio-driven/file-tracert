using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Paging;
using Microsoft.AspNetCore.Mvc;

namespace FileTracert.Host.Controllers;

/// <summary>Queue operation endpoints: enqueue, preview, list, cancel.</summary>
[ApiController]
[Route("api/operations")]
public sealed class OperationsController : ControllerBase
{
    private readonly IQueueService _queue;
    private readonly ILogger<OperationsController> _logger;

    public OperationsController(IQueueService queue, ILogger<OperationsController> logger)
    {
        _queue = queue;
        _logger = logger;
    }

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
        try
        {
            var dto = await _queue.EnqueueAsync(req, ct);
            return CreatedAtAction(nameof(List), new { }, dto);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // §9: surfaced to the user AND logged in full. The message alone reaches the client;
            // the stack (and any inner exception) only exists here.
            _logger.LogWarning(ex, "Enqueue of a {Type} operation rejected as invalid.", req.Type);
            return BadRequest(new { error = ex.Message });
        }
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
        try
        {
            var dtos = await _queue.EnqueueBatchAsync(reqs, ct);
            return CreatedAtAction(nameof(List), new { }, dtos);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // §9: surfaced to the user AND logged in full. The message alone reaches the client;
            // the stack (and the inner exception naming the real cause) only exists here.
            _logger.LogWarning(ex, "Batch enqueue of {Count} operations rejected as invalid.", reqs.Count);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Computes feasibility for an operation without creating any DB record.
    /// Safe to call from the UI "confirm" dialog.
    /// </summary>
    [HttpPost("preview")]
    public async Task<ActionResult<FeasibilityResult>> Preview(
        [FromBody] CreateJobRequest req,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _queue.PreviewAsync(req, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Computes feasibility for a whole batch of operations as one aggregated demand
    /// (required bytes summed per target volume). The UI confirm dialog uses this so the
    /// verdict reflects the entire selection, not just the first file.
    /// </summary>
    [HttpPost("preview-batch")]
    public async Task<ActionResult<FeasibilityResult>> PreviewBatch(
        [FromBody] List<CreateJobRequest> reqs,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _queue.PreviewBatchAsync(reqs, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Puts a Blocked or Failed job back in queue for another attempt (Riprova).
    /// Returns 400 for non-retryable states (Completed, Cancelled, already queued/running).
    /// </summary>
    [HttpPost("{id:int}/retry")]
    public async Task<ActionResult<OperationJobDto>> Retry(int id, CancellationToken ct)
    {
        try
        {
            return Ok(await _queue.RetryAsync(id, ct));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Cancels a non-terminal job and releases its ledger reservation.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        try
        {
            await _queue.CancelAsync(id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
