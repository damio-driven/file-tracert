using FileTracert.Contracts.Enums;

namespace FileTracert.Data.Entities;

/// <summary>
/// A background event surfaced to the user (e.g. a scan that failed for one volume).
/// Lives in the main DB: low volume, domain-linked, consistent with the actions that
/// produce it. <see cref="Message"/> carries the real (unsanitized) detail — this is
/// a single-user power tool and the user wants to see exactly what happened.
/// </summary>
public class Notification : IAuditable
{
    public int Id { get; set; }

    public DateTime TimestampUtc { get; set; }
    public NotificationSeverity Severity { get; set; }

    /// <summary>Origin subsystem, e.g. "Scan", "VolumeSync".</summary>
    public string Source { get; set; } = null!;

    public string Title { get; set; } = null!;

    /// <summary>The real error/detail, not sanitized.</summary>
    public string Message { get; set; } = null!;

    /// <summary>Optional volume context.</summary>
    public int? VolumeId { get; set; }

    public bool IsRead { get; set; }
    public bool IsDismissed { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
