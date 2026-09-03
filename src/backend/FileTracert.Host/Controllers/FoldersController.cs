using FileTracert.Business.Setup;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
using Microsoft.AspNetCore.Mvc;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Browses the real filesystem of an online volume for the setup picker (lazy,
/// one level). Offline → 409; invalid/traversal path → 400.
/// </summary>
[ApiController]
[Route("api/volumes/{volumeId:int}/folders")]
public sealed class FoldersController : ControllerBase
{
    private readonly FolderBrowseService _browse;

    public FoldersController(FolderBrowseService browse) => _browse = browse;

    [HttpGet]
    public async Task<ActionResult<PagedResult<FolderNodeDto>>> Get(
        int volumeId,
        [FromQuery] string path = "",
        [FromQuery] int skip = 0,
        [FromQuery] int take = PagedRequest.DefaultTake,
        CancellationToken ct = default)
    {
        try
        {
            var paged = new PagedRequest(skip, take).Normalized();
            return Ok(await _browse.ListAsync(volumeId, path, paged, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidPathException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (VolumeOfflineException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
