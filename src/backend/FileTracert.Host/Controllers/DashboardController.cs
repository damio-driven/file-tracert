using FileTracert.Business.Dashboard;
using FileTracert.Contracts.Dtos;
using FileTracert.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Dashboard aggregates. Counts come straight from the index and the queue via
/// scalar/aggregate queries (no row materialization).
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
        // One aggregate per table, never two (E6/C30). Files is the biggest table in the database
        // and the one the scan is writing to; counting it and summing it separately walked it
        // twice for one card. Same rule for the volumes and for the queue.
        //
        // Only catalogued, still-present files count toward the totals.
        var catalog = await CatalogTotals.ComputeAsync(
            _db.Files.AsNoTracking().Where(f => f.IsIncluded && f.IsPresent), ct);

        var volumes = await VolumeTotals.ComputeAsync(_db.Volumes.AsNoTracking(), ct);

        var queue = await QueueTotals.ComputeAsync(_db.OperationJobs.AsNoTracking(), ct);

        return Ok(DashboardStatsAssembler.From(
            catalog.TotalFiles, catalog.TotalBytes, volumes.Online, volumes.Total, queue));
    }
}
