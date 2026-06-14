using FileTracert.Business.Setup;
using FileTracert.Contracts.Dtos;
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
    public async Task<ActionResult<IReadOnlyList<FolderNodeDto>>> Get(
        int volumeId, [FromQuery] string path = "", CancellationToken ct = default)
    {
        try
        {
            return Ok(await _browse.ListAsync(volumeId, path, ct));
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
