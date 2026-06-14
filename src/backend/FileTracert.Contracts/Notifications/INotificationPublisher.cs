using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Notifications;

/// <summary>
/// Writes a user-facing notification for a background event (e.g. a scan that failed
/// for one volume). Used <em>in addition to</em> logging when a resilience catch
/// swallows an error that the user should still see. Implementations persist; the
/// caller keeps going.
/// </summary>
public interface INotificationPublisher
{
    Task PublishAsync(
        NotificationSeverity severity,
        string source,
        string title,
        string message,
        int? volumeId,
        CancellationToken ct);
}
