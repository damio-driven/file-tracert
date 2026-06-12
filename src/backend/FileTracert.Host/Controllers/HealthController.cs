using System.Reflection;
using FileTracert.Contracts.Dtos;
using FileTracert.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>Diagnostic liveness endpoint (token-protected like everything else).</summary>
[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly FileTracertDbContext _db;

    public HealthController(FileTracertDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<HealthResponse>> Get(CancellationToken ct)
    {
        var dbState = await _db.Database.CanConnectAsync(ct) ? "ok" : "unreachable";
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        return Ok(new HealthResponse("running", version, dbState));
    }
}
