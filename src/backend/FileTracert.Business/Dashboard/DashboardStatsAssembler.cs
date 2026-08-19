using FileTracert.Contracts.Dtos;

namespace FileTracert.Business.Dashboard;

/// <summary>
/// Pure assembly of the <see cref="DashboardStatsDto"/> from the index aggregates and the
/// queue totals. The queue figures used to be hard-coded zeros ("placeholder step 8"): the
/// queue has shipped since, so the Dashboard was contradicting the Coda screen (C30).
/// </summary>
public static class DashboardStatsAssembler
{
    public static DashboardStatsDto From(
        long totalFiles,
        long totalBytes,
        int volumesOnline,
        int volumesTotal,
        QueueTotals queue) => new(
        totalFiles,
        totalBytes,
        volumesOnline,
        volumesTotal,
        queue.QueuedJobs,
        queue.BlockedJobs,
        queue.RunningJobs,
        queue.PendingBytes);
}
