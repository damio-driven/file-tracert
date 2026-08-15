using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
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

        // Sub-directories of the current node, by PROJECTED position (§5): a folder with a queued
        // move is listed under its destination, not where it still physically sits. The predicate
        // is spelled as an OR rather than a COALESCE so the (VolumeId, ParentId) index still
        // answers the overwhelmingly common no-overlay half.
        // Visibility: on disk (materialized AND present) OR carrying an overlay — that second
        // half is what makes a queued CreateFolder navigable before it exists.
        var subDirs = await _db.Directories
            .AsNoTracking()
            .Where(d => ((d.ParentId == parentId && d.PendingParentId == null) || d.PendingParentId == parentId) &&
                        ((d.IsMaterialized && d.IsPresent) || d.PendingState != EntityPendingState.None))
            // Projected name — the rule lives in FileTracert.Business/Projection/Projected.cs.
            .OrderBy(d => d.PendingName ?? d.Name)
            .Select(d => new
            {
                d.Id,
                Name = d.PendingName ?? d.Name,
                d.MaterializedPath,
                d.PendingState,
                d.PendingJobId,
                // The counters use the same projected predicates as the listings, or a "3 file"
                // badge sits above a list of four.
                ChildCount = _db.Directories.Count(c =>
                    ((c.ParentId == d.Id && c.PendingParentId == null) || c.PendingParentId == d.Id) &&
                    ((c.IsMaterialized && c.IsPresent) || c.PendingState != EntityPendingState.None)),
                FileCount = _db.Files.Count(f =>
                    ((f.DirectoryId == d.Id && f.PendingDirectoryId == null) || f.PendingDirectoryId == d.Id) &&
                    f.IsIncluded && f.IsPresent),
            })
            .ToListAsync(ct);

        var dirDtos = subDirs
            .Select(d => new CatalogDirDto(
                d.Id, d.Name, d.MaterializedPath, d.ChildCount, d.FileCount,
                d.PendingState.ToString(), d.PendingJobId))
            .ToList();

        // Files in the current directory by projected position, paged.
        var filesQuery = _db.Files
            .AsNoTracking()
            .Where(f => ((f.DirectoryId == parentId && f.PendingDirectoryId == null) || f.PendingDirectoryId == parentId) &&
                        f.IsIncluded && f.IsPresent);

        var totalFiles = await filesQuery.CountAsync(ct);

        var filePage = await filesQuery
            .OrderBy(f => f.PendingName ?? f.Name)
            .Skip(paged.Skip)
            .Take(paged.Take)
            .Select(f => new
            {
                f.Id,
                Name = f.PendingName ?? f.Name,
                f.SizeBytes,
                f.FileModifiedUtc,
                f.Category,
                f.PendingState,
                f.PendingJobId,
            })
            .ToListAsync(ct);

        var fileDtos = filePage
            .Select(f => new CatalogFileDto(
                f.Id, f.Name, f.SizeBytes, f.FileModifiedUtc, f.Category,
                f.PendingState.ToString(), f.PendingJobId))
            .ToList();

        var pagedFiles = new PagedResult<CatalogFileDto>(fileDtos, totalFiles, paged.Skip, paged.Take);

        return Ok(new CatalogChildrenDto(dirDtos, pagedFiles, volume.IsOnline, volume.Label, volume.LastDriveLetter, directoryId, parentPath));
    }

    private static PagedResult<CatalogFileDto> EmptyPage(PagedRequest paged) =>
        new([], 0, paged.Skip, paged.Take);
}
