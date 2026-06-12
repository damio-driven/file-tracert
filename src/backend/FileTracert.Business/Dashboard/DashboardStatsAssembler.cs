using FileTracert.Contracts.Dtos;

namespace FileTracert.Business.Dashboard;

/// <summary>
/// Pure assembly of the <see cref="DashboardStatsDto"/> from the index aggregates.
/// The queue figures are forced to 0 here: the operation queue doesn't exist until
/// step 8, so this is the single, documented place those placeholders live.
/// </summary>
public static class DashboardStatsAssembler
{
    public static DashboardStatsDto From(
        long totalFiles,
        long totalBytes,
        int volumesOnline,
        int volumesTotal) => new(
        totalFiles,
        totalBytes,
        volumesOnline,
        volumesTotal,
        // Queue placeholders — replaced when the queue lands (step 8).
        QueuedJobs: 0,
        BlockedJobs: 0,
        RunningJobs: 0,
        PendingBytes: 0);
}
