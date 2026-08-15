using FileTracert.Business.Filtering;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Host.Workers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Volumes read API + rescan trigger. Freshness is applied by the pure
/// <see cref="VolumeFreshnessMapper"/>; per-volume file counts are computed with a
/// single grouped query (no N+1).
/// </summary>
[ApiController]
[Route("api/volumes")]
public sealed class VolumesController : ControllerBase
{
    private readonly FileTracertDbContext _db;
    private readonly IScanScheduler _scanScheduler;

    public VolumesController(FileTracertDbContext db, IScanScheduler scanScheduler)
    {
        _db = db;
        _scanScheduler = scanScheduler;
    }

    /// <summary>Online volumes first, then offline; file counts in one grouped query.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VolumeDto>>> Get(CancellationToken ct)
    {
        var counts = await _db.Files
            .Where(f => f.IsIncluded && f.IsPresent)
            .GroupBy(f => f.VolumeId)
            .Select(g => new { VolumeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VolumeId, x => x.Count, ct);

        var volumes = await _db.Volumes
            .AsNoTracking()
            .OrderByDescending(v => v.IsOnline)
            .ThenBy(v => v.Label)
            .ToListAsync(ct);

        var dtos = volumes
            .Select(v => VolumeFreshnessMapper.ToDto(v, counts.GetValueOrDefault(v.Id)))
            .ToList();

        return Ok(dtos);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<VolumeDetailDto>> GetById(int id, CancellationToken ct)
    {
        var volume = await _db.Volumes
            .AsNoTracking()
            .Include(v => v.WatchedRoots)
            .FirstOrDefaultAsync(v => v.Id == id, ct);

        if (volume is null)
        {
            return NotFound();
        }

        // Index statistics for this volume in one aggregate pass.
        var fileAgg = await _db.Files
            .Where(f => f.VolumeId == id && f.IsIncluded && f.IsPresent)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Bytes = g.Sum(f => f.SizeBytes) })
            .FirstOrDefaultAsync(ct);

        // Same rule as the Catalog tree: directories no longer on disk stop counting,
        // unless a queued operation still holds them in the projection.
        var directoryCount = await _db.Directories
            .CountAsync(d => d.VolumeId == id && d.IsMaterialized &&
                             (d.IsPresent || d.PendingState != EntityPendingState.None), ct);

        var roots = await BuildWatchedRootsAsync(volume, ct);

        var dto = VolumeFreshnessMapper.ToDetailDto(
            volume,
            roots,
            directoryCount,
            fileAgg?.Count ?? 0,
            fileAgg?.Bytes ?? 0);

        return Ok(dto);
    }

    /// <summary>Queue a full rescan of the volume. 202 if accepted, 404 if unknown.</summary>
    [HttpPost("{id:int}/rescan")]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct)
    {
        var exists = await _db.Volumes.AnyAsync(v => v.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        _scanScheduler.RequestScan(id);
        return Accepted();
    }

    /// <summary>
    /// Manually override the catalogable flag (re-enable a false positive or exclude a
    /// volume). 204 on success, 404 if unknown. The sync preserves the override afterwards.
    /// </summary>
    [HttpPost("{id:int}/catalogable")]
    public async Task<IActionResult> SetCatalogable(int id, [FromBody] SetCatalogableRequest request, CancellationToken ct)
    {
        var volume = await _db.Volumes.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (volume is null)
        {
            return NotFound();
        }

        volume.IsCatalogable = request.IsCatalogable;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Resolve each monitored root's effective filter into the display summary.</summary>
    private async Task<IReadOnlyList<WatchedRootDto>> BuildWatchedRootsAsync(Volume volume, CancellationToken ct)
    {
        if (volume.WatchedRoots.Count == 0)
        {
            return [];
        }

        var settings = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppSettings();
        var extensionToCategory = await _db.ExtensionCategories
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Extension, e => e.Category, ct);

        return volume.WatchedRoots
            .OrderBy(r => r.RelativePath)
            .Select(r => new WatchedRootDto(
                r.Id,
                r.RelativePath,
                r.IsActive,
                EffectiveFilterDescriber.Describe(
                    EffectiveFilterBuilder.Build(settings, r.FilterOverrideJson),
                    extensionToCategory)))
            .ToList();
    }
}
