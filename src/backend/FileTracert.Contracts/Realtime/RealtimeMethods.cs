namespace FileTracert.Contracts.Realtime;

/// <summary>
/// The client-side method names the hub invokes. Shared so the publisher, the tests and the
/// TypeScript client all spell them the same way (§7).
/// </summary>
public static class RealtimeMethods
{
    public const string VolumeStatusChanged = "VolumeStatusChanged";
    public const string JobProgress = "JobProgress";
    public const string JobStateChanged = "JobStateChanged";
    public const string ScanProgress = "ScanProgress";
    public const string ProjectionChanged = "ProjectionChanged";
    public const string NotificationRaised = "NotificationRaised";
}
