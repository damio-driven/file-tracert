using FileTracert.Business.Projection;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Host.Controllers;

/// <summary>FTS5-powered file search with scope, filters, paging, and sort.</summary>
[ApiController]
[Route("api/search")]
public sealed class SearchController : ControllerBase
{
    private readonly IFileSearchIndex _fts;
    private readonly FileTracertDbContext _db;
    private readonly ProjectedPathResolver _paths;
    private readonly ILogger<SearchController> _logger;

    public SearchController(
        IFileSearchIndex fts,
        FileTracertDbContext db,
        ProjectedPathResolver paths,
        ILogger<SearchController> logger)
    {
        _fts = fts;
        _db = db;
        _paths = paths;
        _logger = logger;
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

        if (req.Text.Length > 500)
            return BadRequest("text must be 500 characters or fewer");

        if (req.ModifiedFrom?.Kind == DateTimeKind.Unspecified ||
            req.ModifiedTo?.Kind == DateTimeKind.Unspecified)
            return BadRequest("ModifiedFrom and ModifiedTo must be UTC (append 'Z' to the date string)");

        var paged = new PagedRequest(req.Skip, req.Take).Normalized();

        var query = req.ToQuery(paged.Skip, paged.Take);

        var pagedIds = await _fts.SearchAsync(query, ct);

        if (pagedIds.Items.Count == 0)
            return Ok(new PagedResult<SearchResultDto>([], pagedIds.TotalCount, pagedIds.Skip, pagedIds.Take));

        // Fetch full file + volume data for the returned IDs.
        var ids = pagedIds.Items.ToHashSet();
        var rows = await _db.Files
            .AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .Include(f => f.Directory)
            .ToListAsync(ct);

        // Preserve the FTS relevance/sort order.
        var byId = rows.ToDictionary(f => f.Id);

        var phantomCount = ids.Count - rows.Count;
        if (phantomCount > 0)
            _logger.LogWarning("FTS index has {Count} stale entries (file IDs with no DB row)", phantomCount);

        // §5 — a result is described by its PROJECTION: the directory a queued move points at
        // (which may live on another volume), with every overlay on the way up applied.
        var located = await _paths.ResolveDirectoriesAsync(
            rows.Select(Projected.DirectoryIdOf).Distinct().ToList(), ct);

        var volumeIds = located.Values.Select(l => l.VolumeId).Distinct().ToList();
        var volumes = await _db.Volumes.AsNoTracking()
            .Where(v => volumeIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);

        var dtos = pagedIds.Items
            .Where(id => byId.ContainsKey(id))
            .Select(id =>
            {
                var f = byId[id];
                var name = Projected.NameOf(f);
                // Falls back to the physical directory when the projected one cannot be
                // resolved — a search must still return a row, never a 500.
                var location = located.TryGetValue(Projected.DirectoryIdOf(f), out var loc)
                    ? loc
                    : new ProjectedLocation(f.Directory.MaterializedPath, f.VolumeId);
                var volume = volumes.GetValueOrDefault(location.VolumeId);

                return new SearchResultDto(
                    f.Id,
                    name,
                    ScanPath.Join(location.Path, name),
                    location.VolumeId,
                    volume?.Label,
                    volume?.LastDriveLetter,
                    volume?.IsOnline ?? false,
                    f.SizeBytes,
                    f.FileModifiedUtc,
                    f.Category,
                    f.PendingState.ToString(),
                    f.PendingJobId);
            })
            .ToList();

        return Ok(new PagedResult<SearchResultDto>(dtos, pagedIds.TotalCount, pagedIds.Skip, pagedIds.Take));
    }
}
