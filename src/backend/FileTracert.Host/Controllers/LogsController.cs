using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Logging;
using FileTracert.Contracts.Paging;
using FileTracert.Data;
using FileTracert.Host.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Diagnostics surface over the dedicated log database: a paged/filterable view of
/// the log lines plus runtime control of the minimum level (applied immediately and
/// persisted in <c>AppSettings</c> so it survives a restart).
/// </summary>
[ApiController]
[Route("api/logs")]
public sealed class LogsController : ControllerBase
{
    private readonly ILogStore _logStore;
    private readonly LogLevelSwitch _levelSwitch;
    private readonly FileTracertDbContext _db;

    public LogsController(ILogStore logStore, LogLevelSwitch levelSwitch, FileTracertDbContext db)
    {
        _logStore = logStore;
        _levelSwitch = levelSwitch;
        _db = db;
    }

    /// <summary>Newest-first page of log lines, filtered by minimum level/category/search/time.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<LogEntryDto>>> Get(
        [FromQuery] int skip = 0,
        [FromQuery] int take = PagedRequest.DefaultTake,
        [FromQuery] string? level = null,
        [FromQuery] string? category = null,
        [FromQuery] string? search = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        var page = new PagedRequest(skip, take).Normalized();
        var query = new LogQuery(
            page.Skip,
            page.Take,
            MinLevel: LogLevelNames.TryParse(level),
            Category: string.IsNullOrWhiteSpace(category) ? null : category,
            Search: string.IsNullOrWhiteSpace(search) ? null : search,
            FromUtc: fromUtc,
            ToUtc: toUtc);

        return Ok(await _logStore.QueryAsync(query, ct));
    }

    /// <summary>Current runtime minimum log level.</summary>
    [HttpGet("level")]
    public ActionResult<LogLevelDto> GetLevel() =>
        Ok(new LogLevelDto(_levelSwitch.Current.ToString()));

    /// <summary>Sets the runtime minimum log level (immediate) and persists it.</summary>
    [HttpPut("level")]
    public async Task<ActionResult<LogLevelDto>> SetLevel([FromBody] LogLevelDto request, CancellationToken ct)
    {
        if (LogLevelNames.TryParse(request.Level) is not { } parsed)
        {
            return BadRequest($"Unknown log level '{request.Level}'.");
        }

        var name = LogLevelNames.ToName(parsed);
        _levelSwitch.Current = (LogLevel)parsed;

        var settings = await _db.AppSettings.FirstOrDefaultAsync(ct);
        if (settings is not null)
        {
            settings.MinimumLogLevel = name;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new LogLevelDto(name));
    }
}
