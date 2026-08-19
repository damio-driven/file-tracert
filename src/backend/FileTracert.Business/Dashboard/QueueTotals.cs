using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Dashboard;

/// <summary>
/// The queue as the Dashboard cards state it (C30). Until now these four figures were
/// hard-coded zeros left over from before the queue shipped, so the Dashboard said "nothing
/// queued" while the Coda screen listed real jobs.
///
/// Definitions, because each card claims something specific:
/// <list type="bullet">
/// <item><see cref="QueuedJobs"/> — every job that is not terminal: the headline "how much is
/// still in the queue", of which running and blocked are the breakdown underneath.</item>
/// <item><see cref="RunningJobs"/> — actually moving bytes right now (Copying / Verifying /
/// DeletingSource). Pending and SpaceReserved are queued, not running.</item>
/// <item><see cref="BlockedJobs"/> — parked for any reason, space and dependency alike.</item>
/// <item><see cref="PendingBytes"/> — bytes still to write for the jobs parked on a RESOURCE
/// (space or an unplugged volume), which is exactly what the card above it says. A job waiting
/// for another job is counted in <see cref="BlockedJobs"/> but not here: nothing about it is
/// waiting for room on a disk.</item>
/// </list>
/// </summary>
public sealed record QueueTotals(int QueuedJobs, int BlockedJobs, int RunningJobs, long PendingBytes)
{
    public static readonly QueueTotals Empty = new(0, 0, 0, 0);

    /// <summary>Reasons that make a blocked job's bytes "waiting for space or a volume".</summary>
    private static readonly JobBlockReason[] ResourceReasons =
    [
        JobBlockReason.InsufficientSpace,
        JobBlockReason.TargetVolumeOffline,
        JobBlockReason.SourceVolumeOffline,
    ];

    /// <summary>
    /// All four figures in ONE aggregate round trip — four counts of the same table would be
    /// four scans for a card strip that is read on every Dashboard load and on every queue
    /// transition. <c>GroupBy(_ => 1)</c> is the EF idiom for "aggregate the whole table"; it
    /// yields no row when there are no jobs at all, which is what <see cref="Empty"/> covers.
    /// </summary>
    public static async Task<QueueTotals> ComputeAsync(
        IQueryable<OperationJob> jobs, CancellationToken ct)
    {
        var totals = await jobs
            .GroupBy(_ => 1)
            .Select(g => new QueueTotals(
                g.Count(j => !JobStates.Terminal.Contains(j.State)),
                g.Count(j => j.State == JobState.Blocked),
                g.Count(j => JobStates.Active.Contains(j.State)),
                // Clamped by hand rather than with Math.Max: a checkpoint that overshoots
                // TotalBytes (a folder move whose subtree grew) must not subtract from the total.
                g.Sum(j => j.State == JobState.Blocked
                        && ResourceReasons.Contains(j.BlockReason)
                        && j.TotalBytes > j.BytesProcessed
                    ? j.TotalBytes - j.BytesProcessed
                    : 0L)))
            .FirstOrDefaultAsync(ct);

        return totals ?? Empty;
    }
}
