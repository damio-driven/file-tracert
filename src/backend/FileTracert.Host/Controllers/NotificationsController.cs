using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>
/// User-facing notifications surfaced by background workers. Dismissed entries are
/// hidden; the bell badge reads the unread count. (At step 10 these become SignalR
/// pushes; for now the UI fetches and refreshes.)
/// </summary>
[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly FileTracertDbContext _db;

    public NotificationsController(FileTracertDbContext db) => _db = db;

    /// <summary>Newest-first page of non-dismissed notifications; <paramref name="unread"/> filters to unread.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<NotificationDto>>> Get(
        [FromQuery] int skip = 0,
        [FromQuery] int take = PagedRequest.DefaultTake,
        [FromQuery] bool unread = false,
        CancellationToken ct = default)
    {
        var page = new PagedRequest(skip, take).Normalized();

        var query = _db.Notifications.AsNoTracking().Where(n => !n.IsDismissed);
        if (unread)
        {
            query = query.Where(n => !n.IsRead);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(n => n.TimestampUtc)
            .ThenByDescending(n => n.Id)
            .Skip(page.Skip)
            .Take(page.Take)
            .Select(n => ToDto(n))
            .ToListAsync(ct);

        return Ok(new PagedResult<NotificationDto>(items, total, page.Skip, page.Take));
    }

    /// <summary>Unread, non-dismissed count for the bell badge.</summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<NotificationCountDto>> UnreadCount(CancellationToken ct)
    {
        var count = await _db.Notifications.CountAsync(n => !n.IsRead && !n.IsDismissed, ct);
        return Ok(new NotificationCountDto(count));
    }

    /// <summary>Marks one notification read. 404 if unknown.</summary>
    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct) =>
        await MutateAsync(id, n => n.IsRead = true, ct);

    /// <summary>Dismisses one notification (also marks it read). 404 if unknown.</summary>
    [HttpPost("{id:int}/dismiss")]
    public async Task<IActionResult> Dismiss(int id, CancellationToken ct) =>
        await MutateAsync(id, n =>
        {
            n.IsRead = true;
            n.IsDismissed = true;
        }, ct);

    private async Task<IActionResult> MutateAsync(int id, Action<Notification> mutate, CancellationToken ct)
    {
        var notification = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (notification is null)
        {
            return NotFound();
        }

        mutate(notification);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static NotificationDto ToDto(Notification n) => new(
        n.Id,
        n.TimestampUtc,
        n.Severity,
        n.Source,
        n.Title,
        n.Message,
        n.VolumeId,
        n.IsRead,
        n.IsDismissed);
}
