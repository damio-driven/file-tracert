using FileTracert.Contracts.Scanning;
using Microsoft.AspNetCore.Mvc;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Live scan-progress polling. Reads the in-memory <see cref="IScanStatusTracker"/>
/// (no DB hit) so the UI can show per-volume phase and counts while a scan runs.
/// Polling is interim — at step 10 the same shape is pushed over SignalR.
/// </summary>
[ApiController]
[Route("api/scans")]
public sealed class ScansController : ControllerBase
{
    private readonly IScanStatusTracker _tracker;

    public ScansController(IScanStatusTracker tracker)
    {
        _tracker = tracker;
    }

    /// <summary>Scans currently in flight; an empty list means nothing is scanning.</summary>
    [HttpGet("status")]
    public ActionResult<IReadOnlyList<ScanStatusDto>> Status() => Ok(_tracker.Snapshot());
}
