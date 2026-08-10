using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Re-evaluates the jobs parked in <c>Blocked</c> for a reason that can resolve on its own —
/// missing space, or a volume that was not connected — and returns them to <c>Pending</c> when the
/// obstacle is gone. §4: Blocked jobs are re-evaluated on every event. Today those events are the
/// completion of a job (the queue worker) and a volume coming back online (the volume sync);
/// step 10 will add the real-time device-watcher push in front of the same entry point.
///
/// Two gates, in this order:
///  1. <see cref="VolumeOfflineGate"/> — a job whose volumes are not all connected stays blocked,
///     with the reason retargeted to whichever volume is missing NOW.
///  2. HARD feasibility (no credit for promised liberations of jobs that have not completed) —
///     so an unblocked job is guaranteed to pass the engine's execution-time re-check instead of
///     ping-ponging Blocked → Pending → Blocked. At a remount this is the "never copy on an
///     estimate" re-check of §4: the volume sync refreshes FreeBytesLastKnown from the live probe
///     before this runs, so the figure evaluated here is the drive's real free space.
/// </summary>
public sealed class BlockedJobRevaluator
{
    private static readonly JobBlockReason[] RevaluableReasons =
    [
        JobBlockReason.InsufficientSpace,
        JobBlockReason.TargetVolumeOffline,
        JobBlockReason.SourceVolumeOffline,
    ];

    private readonly FileTracertDbContext _db;
    private readonly ISpaceLedger _ledger;
    private readonly ILogger<BlockedJobRevaluator> _logger;

    public BlockedJobRevaluator(
        FileTracertDbContext db,
        ISpaceLedger ledger,
        ILogger<BlockedJobRevaluator> logger)
    {
        _db = db;
        _ledger = ledger;
        _logger = logger;
    }

    /// <summary>Returns the number of jobs moved back to Pending.</summary>
    public async Task<int> RevaluateAsync(CancellationToken ct)
    {
        var blocked = await _db.OperationJobs
            .Include(j => j.SourceVolume)
            .Include(j => j.TargetVolume)
            .Where(j => j.State == JobState.Blocked && RevaluableReasons.Contains(j.BlockReason))
            .OrderBy(j => j.SequenceOrder)
            .ToListAsync(ct);

        int unblockedCount = 0;

        // FIFO order matters: a job unblocked here re-enters the ledger demand that the
        // feasibility of the next candidate must account for.
        foreach (var job in blocked)
        {
            var offline = VolumeOfflineGate.Evaluate(job.SourceVolume, job.TargetVolume);
            if (offline != JobBlockReason.None)
            {
                await KeepBlockedAsync(job, offline,
                    VolumeOfflineGate.Describe(offline, job.SourceVolume, job.TargetVolume), ct);
                continue;
            }

            var deficit = await HardSpaceDeficitAsync(job, ct);
            if (deficit > 0)
            {
                await KeepBlockedAsync(job, JobBlockReason.InsufficientSpace,
                    $"Insufficient space: {deficit} bytes short on volume {job.TargetVolumeId}.", ct);
                continue;
            }

            if (await UnblockAsync(job, ct))
                unblockedCount++;
        }

        return unblockedCount;
    }

    /// <summary>
    /// Missing bytes on the target under the HARD view, or 0 when the job needs no space
    /// (intra-volume ops, and any job with nothing to reserve).
    /// </summary>
    private async Task<long> HardSpaceDeficitAsync(OperationJob job, CancellationToken ct)
    {
        if (job.IsIntraVolume || job.RequiredBytesTarget <= 0 || job.TargetVolume is null)
            return 0;

        var tgtVol = job.TargetVolume;
        var feasibility = await _ledger.ComputeFeasibilityAsync(
            tgtVol.Id, tgtVol.FreeBytesLastKnown, tgtVol.IsOnline, job.RequiredBytesTarget,
            excludeJobId: job.Id, sequenceOrder: job.SequenceOrder,
            includeQueuedLiberations: false, ct);

        return feasibility.DeficitBytes;
    }

    /// <summary>Returns the job to Pending and normalizes its reservation. False if a concurrent write won.</summary>
    private async Task<bool> UnblockAsync(OperationJob job, CancellationToken ct)
    {
        var previousReason = job.BlockReason;
        job.State = JobState.Pending;
        job.BlockReason = JobBlockReason.None;
        job.ErrorMessage = null;
        if (!await SaveOrFollowConcurrentStateAsync(job, ct))
            return false;

        // Guarantee exactly one active reservation: a job blocked at enqueue for lack of space
        // never reserved (shouldReserve was false), one blocked by the engine — or parked by the
        // offline gate — kept its reservation. Release-then-reserve normalizes both cases.
        if (!job.IsIntraVolume && job.RequiredBytesTarget > 0 && job.TargetVolumeId.HasValue)
        {
            await _ledger.ReleaseAsync(job.Id, ct);
            await _ledger.ReserveAsync(
                job.Id, job.SequenceOrder, job.TargetVolumeId.Value,
                job.RequiredBytesTarget, job.SourceVolumeId, job.FreedBytesSource, ct);
        }

        _logger.LogInformation(
            "Job {Id} unblocked: the obstacle is gone (was {Reason}).", job.Id, previousReason);
        return true;
    }

    /// <summary>
    /// The job stays blocked, but possibly for a different reason than before — the target came
    /// back while the source is still missing, or a remount revealed the drive is now too full.
    /// Persisted only when something actually changed, so a quiet revaluation writes nothing.
    /// </summary>
    private async Task KeepBlockedAsync(OperationJob job, JobBlockReason reason, string message, CancellationToken ct)
    {
        if (job.BlockReason == reason && job.ErrorMessage == message)
            return;

        _logger.LogInformation(
            "Job {Id} stays blocked: {Old} → {New}.", job.Id, job.BlockReason, reason);

        job.BlockReason = reason;
        job.ErrorMessage = message;
        await SaveOrFollowConcurrentStateAsync(job, ct);
    }

    /// <summary>
    /// Saves, or — when the <c>State</c> concurrency token trips because a cancel/engine transition
    /// raced us — keeps the committed state, reloads and reports failure so the caller skips this
    /// job. The revaluation pass stays alive for the remaining candidates.
    /// </summary>
    private async Task<bool> SaveOrFollowConcurrentStateAsync(OperationJob job, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await _db.Entry(job).ReloadAsync(ct);
            _logger.LogInformation(ex,
                "Job {Id}: state moved concurrently during revaluation (now {State}) — skipped.",
                job.Id, job.State);
            return false;
        }
    }
}
