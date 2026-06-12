namespace FileTracert.Contracts.Dtos;

/// <summary>
/// Aggregates for the Dashboard cards + header. The queue fields are deliberate
/// placeholders: the operation queue lands at step 8, so until then
/// <see cref="QueuedJobs"/>, <see cref="BlockedJobs"/>, <see cref="RunningJobs"/>
/// and <see cref="PendingBytes"/> are always 0 — kept (not removed) because the
/// mockup's Dashboard renders these cards.
/// </summary>
public sealed record DashboardStatsDto(
    long TotalFiles,
    long TotalBytes,
    int VolumesOnline,
    int VolumesTotal,
    // --- queue placeholders (step 8) ---
    int QueuedJobs,
    int BlockedJobs,
    int RunningJobs,
    long PendingBytes);
