using FileTracert.Business.Dashboard;
using FileTracert.Contracts.Dtos;
using FileTracert.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Dashboard aggregates. Counts come straight from the index via scalar/aggregate
/// queries (no row materialization). Queue figures are placeholders until step 8.
/// </summary>
[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController : ControllerBase
{
    private readonly FileTracertDbContext _db;

    public DashboardController(FileTracertDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<DashboardStatsDto>> Get(CancellationToken ct)
    {
        // Only catalogued, still-present files count toward the totals.
        var included = _db.Files.Where(f => f.IsIncluded && f.IsPresent);

        var totalFiles = await included.LongCountAsync(ct);
        var totalBytes = totalFiles == 0 ? 0L : await included.SumAsync(f => f.SizeBytes, ct);

        var volumesTotal = await _db.Volumes.CountAsync(ct);
        var volumesOnline = await _db.Volumes.CountAsync(v => v.IsOnline, ct);

        return Ok(DashboardStatsAssembler.From(totalFiles, totalBytes, volumesOnline, volumesTotal));
    }
}
