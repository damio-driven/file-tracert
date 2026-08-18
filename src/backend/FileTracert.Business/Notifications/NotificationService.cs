using FileTracert.Business.Realtime;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
using FileTracert.Data;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Notifications;

/// <summary>
/// Persists notifications to the main database (low volume, domain-linked). The
/// message is stored verbatim — power-user single-user tool, the real detail is the
/// point.
/// </summary>
public sealed class NotificationService : INotificationPublisher
{
    private readonly FileTracertDbContext _db;
    private readonly RealtimeEvents _realtime;

    public NotificationService(FileTracertDbContext db, RealtimeEvents realtime)
    {
        _db = db;
        _realtime = realtime;
    }

    public async Task PublishAsync(
        NotificationSeverity severity,
        string source,
        string title,
        string message,
        int? volumeId,
        CancellationToken ct)
    {
        var notification = new Notification
        {
            TimestampUtc = DateTime.UtcNow,
            Severity = severity,
            Source = source,
            Title = title,
            Message = message,
            VolumeId = volumeId,
            IsRead = false,
            IsDismissed = false,
        };
        _db.Notifications.Add(notification);

        await _db.SaveChangesAsync(ct);

        // After the save: the row (and its Id) exists, so a client that reacts by fetching the
        // bell finds exactly what the push announced.
        await _realtime.NotificationRaisedAsync(notification);
    }
}
