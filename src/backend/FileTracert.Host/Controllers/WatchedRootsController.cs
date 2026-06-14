using FileTracert.Business.Setup;
using FileTracert.Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace FileTracert.Host.Controllers;

/// <summary>Write API for monitored roots: create under a volume, patch, soft-delete.</summary>
[ApiController]
public sealed class WatchedRootsController : ControllerBase
{
    private readonly WatchedRootsService _service;

    public WatchedRootsController(WatchedRootsService service) => _service = service;

    [HttpPost("api/volumes/{volumeId:int}/watched-roots")]
    public async Task<ActionResult<WatchedRootDto>> Create(
        int volumeId, [FromBody] CreateWatchedRootRequest request, CancellationToken ct)
    {
        try
        {
            var dto = await _service.CreateAsync(volumeId, request, ct);
            return CreatedAtAction(nameof(Create), new { volumeId, id = dto.Id }, dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidPathException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (WatchedRootConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPatch("api/watched-roots/{id:int}")]
    public async Task<ActionResult<WatchedRootUpdateResponse>> Update(
        int id, [FromBody] UpdateWatchedRootRequest request, CancellationToken ct)
    {
        try
        {
            var (dto, reconcile) = await _service.UpdateAsync(id, request, ct);
            return Ok(new WatchedRootUpdateResponse(dto, reconcile));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("api/watched-roots/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        try
        {
            await _service.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
