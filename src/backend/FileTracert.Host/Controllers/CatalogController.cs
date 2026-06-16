using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
using FileTracert.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Catalog browser: lazy directory tree over the index.
/// directoryId=null → children of the volume root (MaterializedPath="").
/// Works offline (reads index, not disk).
/// </summary>
[ApiController]
[Route("api/catalog")]
public sealed class CatalogController : ControllerBase
{
    private readonly FileTracertDbContext _db;

    public CatalogController(FileTracertDbContext db) => _db = db;

    [HttpGet("{volumeId:int}/children")]
    public async Task<ActionResult<CatalogChildrenDto>> GetChildren(
        int volumeId,
        [FromQuery] int? directoryId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var volume = await _db.Volumes
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == volumeId, ct);

        if (volume is null)
            return NotFound();

        var paged = new PagedRequest(skip, take).Normalized();

        int parentId;
        string? parentPath;

        if (directoryId is null)
        {
            // Volume root: find the synthetic root node (MaterializedPath == "").
            var root = await _db.Directories
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.VolumeId == volumeId && d.MaterializedPath == string.Empty, ct);

            if (root is null)
            {
                // No index yet for this volume.
                return Ok(new CatalogChildrenDto([], EmptyPage(paged), volume.IsOnline, volume.Label, volume.LastDriveLetter, null, null));
            }

            parentId = root.Id;
            parentPath = null;
        }
        else
        {
            var dir = await _db.Directories
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == directoryId && d.VolumeId == volumeId, ct);

            if (dir is null)
                return NotFound();

            parentId = dir.Id;
            parentPath = dir.MaterializedPath;
        }

        // Sub-directories of the current node.
        var subDirs = await _db.Directories
            .AsNoTracking()
            .Where(d => d.VolumeId == volumeId && d.ParentId == parentId && d.IsMaterialized)
            .OrderBy(d => d.Name)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.MaterializedPath,
                ChildCount = _db.Directories.Count(c => c.ParentId == d.Id && c.IsMaterialized),
                FileCount = _db.Files.Count(f => f.DirectoryId == d.Id && f.IsIncluded && f.IsPresent),
            })
            .ToListAsync(ct);

        var dirDtos = subDirs
            .Select(d => new CatalogDirDto(d.Id, d.Name, d.MaterializedPath, d.ChildCount, d.FileCount))
            .ToList();

        // Files in the current directory, paged.
        var filesQuery = _db.Files
            .AsNoTracking()
            .Where(f => f.DirectoryId == parentId && f.IsIncluded && f.IsPresent);

        var totalFiles = await filesQuery.CountAsync(ct);

        var filePage = await filesQuery
            .OrderBy(f => f.Name)
            .Skip(paged.Skip)
            .Take(paged.Take)
            .Select(f => new CatalogFileDto(f.Id, f.Name, f.SizeBytes, f.FileModifiedUtc, f.Category, "None"))
            .ToListAsync(ct);

        var pagedFiles = new PagedResult<CatalogFileDto>(filePage, totalFiles, paged.Skip, paged.Take);

        return Ok(new CatalogChildrenDto(dirDtos, pagedFiles, volume.IsOnline, volume.Label, volume.LastDriveLetter, directoryId, parentPath));
    }

    private static PagedResult<CatalogFileDto> EmptyPage(PagedRequest paged) =>
        new([], 0, paged.Skip, paged.Take);
}
