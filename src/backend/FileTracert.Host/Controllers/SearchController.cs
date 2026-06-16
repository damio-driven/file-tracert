using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>FTS5-powered file search with scope, filters, paging, and sort.</summary>
[ApiController]
[Route("api/search")]
public sealed class SearchController : ControllerBase
{
    private readonly IFileSearchIndex _fts;
    private readonly FileTracertDbContext _db;

    public SearchController(IFileSearchIndex fts, FileTracertDbContext db)
    {
        _fts = fts;
        _db = db;
    }

    /// <summary>
    /// POST body carries the full query (text, scope, filters, paging).
    /// Returns a paged list of matching files with volume context.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PagedResult<SearchResultDto>>> Search(
        [FromBody] SearchRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest("text is required");

        var query = new FileSearchQuery(
            req.Text, req.Scope, req.Category, req.Extensions,
            req.SizeBytesMin, req.SizeBytesMax,
            req.ModifiedFrom, req.ModifiedTo,
            req.VolumeId, req.OnlineOnly,
            req.Sort, req.Desc, req.Skip, req.Take);

        var pagedIds = await _fts.SearchAsync(query, ct);

        if (pagedIds.Items.Count == 0)
            return Ok(new PagedResult<SearchResultDto>([], pagedIds.TotalCount, pagedIds.Skip, pagedIds.Take));

        // Fetch full file + volume data for the returned IDs.
        var ids = pagedIds.Items.ToHashSet();
        var rows = await _db.Files
            .AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .Include(f => f.Directory)
            .Include(f => f.Volume)
            .ToListAsync(ct);

        // Preserve the FTS relevance/sort order.
        var byId = rows.ToDictionary(f => f.Id);
        var dtos = pagedIds.Items
            .Where(id => byId.ContainsKey(id))
            .Select(id =>
            {
                var f = byId[id];
                var dirPath = f.Directory.MaterializedPath;
                var relativePath = dirPath.Length == 0 ? f.Name : $"{dirPath}\\{f.Name}";
                return new SearchResultDto(
                    f.Id,
                    f.Name,
                    relativePath,
                    f.VolumeId,
                    f.Volume.Label,
                    f.Volume.LastDriveLetter,
                    f.Volume.IsOnline,
                    f.SizeBytes,
                    f.FileModifiedUtc,
                    f.Category,
                    "None");
            })
            .ToList();

        return Ok(new PagedResult<SearchResultDto>(dtos, pagedIds.TotalCount, pagedIds.Skip, pagedIds.Take));
    }
}
