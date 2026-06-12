using FileTracert.Contracts.Dtos;
using FileTracert.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Sanity read of the catalog volumes, to eyeball that volume sync works. The
/// real domain API (paging, freshness contract) replaces this at steps 6–7.
/// </summary>
[ApiController]
[Route("volumes")]
public sealed class VolumesController : ControllerBase
{
    private readonly FileTracertDbContext _db;

    public VolumesController(FileTracertDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VolumeDto>>> Get(CancellationToken ct)
    {
        var volumes = await _db.Volumes
            .AsNoTracking()
            .OrderBy(v => v.Id)
            .Select(v => new VolumeDto(
                v.Id,
                v.VolumeGuid,
                v.SerialNumber,
                v.Label,
                v.FileSystem,
                v.IsRemovable,
                v.PhysicalDiskId,
                v.LastDriveLetter,
                v.CapacityBytes,
                v.FreeBytesLastKnown,
                v.LastSeenUtc,
                v.IsOnline,
                v.ScanEngine.ToString(),
                v.LastUsn,
                v.LastFullScanUtc))
            .ToListAsync(ct);

        return Ok(volumes);
    }
}
