using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Dtos;

/// <summary>A background event surfaced to the user (read/dismiss state included).</summary>
public sealed record NotificationDto(
    int Id,
    DateTime TimestampUtc,
    NotificationSeverity Severity,
    string Source,
    string Title,
    string Message,
    int? VolumeId,
    bool IsRead,
    bool IsDismissed);
