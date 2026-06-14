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

    public NotificationService(FileTracertDbContext db) => _db = db;

    public async Task PublishAsync(
        NotificationSeverity severity,
        string source,
        string title,
        string message,
        int? volumeId,
        CancellationToken ct)
    {
        _db.Notifications.Add(new Notification
        {
            TimestampUtc = DateTime.UtcNow,
            Severity = severity,
            Source = source,
            Title = title,
            Message = message,
            VolumeId = volumeId,
            IsRead = false,
            IsDismissed = false,
        });

        await _db.SaveChangesAsync(ct);
    }
}
