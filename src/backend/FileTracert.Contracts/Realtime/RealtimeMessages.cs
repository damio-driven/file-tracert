using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Realtime;

/// <summary>
/// Server → client push payloads (§7). All of them are deliberately <em>thin</em>: an id plus
/// the handful of fields that actually changed. A client that needs the rest re-issues the GET
/// it already has — pushing whole DTOs on every tick would turn a progress bar into a firehose.
/// Dates are UTC (§6) and enums travel as their names (the hub's JSON protocol is configured
/// with a string enum converter, like the Web API).
/// </summary>
public sealed record VolumeStatusChanged(
    int VolumeId,
    bool IsOnline,
    long FreeBytesLastKnown,
    DateTime LastSeenUtc);

/// <summary>Byte progress of a job being copied. Emitted at the engine's existing save cadence.</summary>
public sealed record JobProgress(
    int JobId,
    long BytesProcessed,
    long TotalBytes);

/// <summary>Every persisted state transition of a job, including enqueue, cancel, retry and block.</summary>
public sealed record JobStateChanged(
    int JobId,
    JobState State,
    JobBlockReason BlockReason,
    string? ErrorMessage);

/// <summary>
/// The <c>Pending*</c> overlay changed (§5), so Catalogo/Ricerca are showing a stale projection.
/// <paramref name="VolumeId"/> is null when the change is not confined to one volume (a
/// cross-volume move touches two) — the client then refreshes whatever it has on screen.
/// </summary>
public sealed record ProjectionChanged(
    int? VolumeId,
    int? JobId);

/// <summary>A new row landed in <c>Notifications</c>; the bell can drop its poll.</summary>
public sealed record NotificationRaised(
    int Id,
    NotificationSeverity Severity,
    string Title,
    DateTime TimestampUtc);
