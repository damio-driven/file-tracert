using FileTracert.Business.Realtime;
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
///  2. HARD feasibility (<see cref="SpaceCheck"/>: no credit for promised liberations of jobs
///     that have not completed, free bytes read from the device) — so an unblocked job is
///     guaranteed to pass the engine's execution-time re-check instead of ping-ponging
///     Blocked → Pending → Blocked. It is the same object the engine consults, over the same
///     live figure: §4's "never copy on an estimate" has to hold on the release side too, or the
///     release is just a promise the engine breaks a second later.
/// </summary>
public sealed class BlockedJobRevaluator
{
    /// <summary>
    /// Block reasons that can resolve without the user doing anything.
    /// <see cref="JobBlockReason.DependencyCancelled"/> is deliberately NOT here: the
    /// prerequisite was cancelled or failed, which is a decision (or a fact) the user has to
    /// acknowledge. Releasing such a job automatically is precisely the failure finding 9
    /// describes — the folder whose creation was cancelled gets silently recreated by the
    /// dependent move. «Riprova» is its reactivation path, and it re-asks the guard.
    /// </summary>
    private static readonly JobBlockReason[] RevaluableReasons =
    [
        JobBlockReason.InsufficientSpace,
        JobBlockReason.TargetVolumeOffline,
        JobBlockReason.SourceVolumeOffline,
        JobBlockReason.DependencyPending,
    ];

    private readonly FileTracertDbContext _db;
    private readonly ISpaceLedger _ledger;
    private readonly SpaceCheck _spaceCheck;
    private readonly JobUnblocker _unblocker;
    private readonly RealtimeEvents _realtime;
    private readonly ILogger<BlockedJobRevaluator> _logger;

    public BlockedJobRevaluator(
        FileTracertDbContext db,
        ISpaceLedger ledger,
        SpaceCheck spaceCheck,
        JobUnblocker unblocker,
        RealtimeEvents realtime,
        ILogger<BlockedJobRevaluator> logger)
    {
        _db = db;
        _ledger = ledger;
        _spaceCheck = spaceCheck;
        _unblocker = unblocker;
        _realtime = realtime;
        _logger = logger;
    }

    /// <summary>Returns the number of jobs moved back to Pending.</summary>
    public async Task<int> RevaluateAsync(CancellationToken ct)
    {
        var blocked = await _db.OperationJobs
            .Include(j => j.Items)
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
            // A job waiting for another job is not waiting for the WORLD: the offline and space
            // gates below would overwrite DependencyPending with a reason that is true but not
            // the blocking one, and lose track of the prerequisite. Settle the dependency first;
            // only a job whose path is clear falls through to the other two gates.
            if (job.BlockReason == JobBlockReason.DependencyPending &&
                !await DependencyIsSettledAsync(job, ct))
            {
                continue;
            }

            var offline = VolumeOfflineGate.Evaluate(job.SourceVolume, job.TargetVolume);
            if (offline != JobBlockReason.None)
            {
                await KeepBlockedAsync(job, offline,
                    VolumeOfflineGate.Describe(offline, job.SourceVolume, job.TargetVolume),
                    job.DependsOnJobId, ct);
                continue;
            }

            // Same hard check the engine runs, over the same live figure: releasing a job on a
            // number the engine would then contradict is how a job ping-pongs Blocked → Pending
            // → Blocked without ever moving a byte.
            var space = await _spaceCheck.EvaluateHardAsync(job, ct);
            if (!space.Ok)
            {
                await KeepBlockedAsync(job, space.Reason, space.Message, job.DependsOnJobId, ct);
                continue;
            }

            if (await UnblockAsync(job, ct))
                unblockedCount++;
        }

        return unblockedCount;
    }

    /// <summary>
    /// Decides what to do with a job parked behind another one. Returns true — and leaves the job
    /// with no dependency — only when the path is genuinely clear; otherwise the job is left (or
    /// re-parked) <c>Blocked</c> and the caller moves on.
    ///
    /// The prerequisite is not trusted as a single yes/no: when it is done, the GUARD is asked
    /// again. That is what makes one <c>DependsOnJobId</c> sufficient — several jobs can overlap
    /// a subtree, and the dependency is simply re-pointed at whoever is still in the way.
    /// </summary>
    private async Task<bool> DependencyIsSettledAsync(OperationJob job, CancellationToken ct)
    {
        if (job.DependsOnJobId is { } prerequisiteId)
        {
            var prerequisite = await _db.OperationJobs.AsNoTracking()
                .Where(j => j.Id == prerequisiteId)
                .Select(j => new { j.Id, j.State, j.Type })
                .FirstOrDefaultAsync(ct);

            if (prerequisite is not null)
            {
                if (!JobStates.Terminal.Contains(prerequisite.State))
                    return false;   // still running or still queued: nothing to do, no write

                if (prerequisite.State is JobState.Cancelled or JobState.Failed)
                {
                    // §5: never a cascade of cancellations — the dependent stays in the queue,
                    // parked on a reason that says why, reactivatable with «Riprova».
                    await KeepBlockedAsync(job, JobBlockReason.DependencyCancelled,
                        $"L'operazione #{prerequisite.Id} ({prerequisite.Type}) da cui dipendeva " +
                        $"è terminata come {prerequisite.State}: verificare e riprovare.",
                        prerequisiteId, ct);
                    return false;
                }
            }
        }

        var conflict = await _unblocker.FindConflictAsync(job, ct);
        if (conflict is not null)
        {
            await KeepBlockedAsync(job, JobBlockReason.DependencyPending,
                QueueService.DescribeDependency(conflict), conflict.JobId, ct);
            return false;
        }

        return true;
    }

    /// <summary>Returns the job to Pending and normalizes its reservation. False if a concurrent write won.</summary>
    private async Task<bool> UnblockAsync(OperationJob job, CancellationToken ct)
    {
        var previousReason = job.BlockReason;

        // A job that waited behind another one was queued with paths the other job has since
        // invalidated (finding 8a), and owns no projection overlay. Both are fixed here, in the
        // SAME transaction as the state change: a crash must never leave a runnable job with a
        // dead snapshot or an invisible operation. Nothing is written until the refresh has
        // succeeded — if it cannot, the job stays parked with an explicit reason, never Failed.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var problem = await _unblocker.RefreshSnapshotsAsync(job, ct);
        if (problem is not null)
        {
            await tx.RollbackAsync(ct);
            await DiscardPendingEditsAsync(job, ct);
            await KeepBlockedAsync(job, job.BlockReason, problem, job.DependsOnJobId, ct);
            return false;
        }

        job.State = JobState.Pending;
        job.BlockReason = JobBlockReason.None;
        job.ErrorMessage = null;
        job.DependsOnJobId = null;
        if (!await SaveOrFollowConcurrentStateAsync(job, ct))
        {
            await tx.RollbackAsync(ct);
            await DiscardPendingEditsAsync(job, ct);
            return false;
        }

        await _unblocker.TakeOverlayAsync(job, ct);

        // Guarantee exactly one active reservation: a job blocked at enqueue for lack of space
        // never reserved (shouldReserve was false), one blocked by the engine — or parked by the
        // offline gate — kept its reservation. Release-then-reserve normalizes both cases.
        //
        // E8 — INSIDE this transaction, on this connection, instead of two more write transactions
        // on scopes of their own after the commit. Three writes per released job became one, and
        // the durable half of the ledger now moves with the state change it belongs to, which is
        // the crash-safety rule WP1 established for the terminal transitions (finding #5): the
        // window in which a crash left a Pending job with its reservation released and not yet
        // re-taken — i.e. under-reserved against everybody else — no longer exists.
        //
        // Nothing here waits on another connection, so this does NOT re-create the deadlock the
        // retry path avoids by committing first: ISpaceLedger.ReserveAsync/ReleaseAsync open their
        // OWN scope and connection, and calling them while holding SQLite's single write lock is
        // what would be a self-inflicted SQLITE_BUSY. The static halves take the caller's context
        // precisely so a unit of work can own them.
        bool normalizeReservation =
            !job.IsIntraVolume && job.RequiredBytesTarget > 0 && job.TargetVolumeId.HasValue;

        if (normalizeReservation)
        {
            await SpaceLedger.DeactivateEntriesAsync(_db, job.Id, ct);
            _db.SpaceLedgerEntries.AddRange(SpaceLedger.BuildReservationEntries(
                job.Id, job.TargetVolumeId!.Value, job.RequiredBytesTarget,
                job.SourceVolumeId, job.FreedBytesSource));
            await _db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);

        // The in-memory mirror follows the commit, never precedes it — same order as the enqueue
        // and the completion. Until this runs the ledger under-counts this job's demand; a
        // revaluation pass is single-threaded and the next candidate is judged after it, so no
        // decision is taken on the gap.
        if (normalizeReservation)
        {
            await _ledger.ReleaseInMemoryAsync(job.Id, ct);
            await _ledger.RegisterReservationInMemoryAsync(
                job.Id, job.SequenceOrder, job.TargetVolumeId!.Value,
                job.RequiredBytesTarget, job.SourceVolumeId, job.FreedBytesSource, ct);
        }

        _logger.LogInformation(
            "Job {Id} unblocked: the obstacle is gone (was {Reason}).", job.Id, previousReason);

        // After the commit. The release also TOOK the overlay (TakeOverlayAsync above), so the
        // projection changed too — a dependent that owned nothing while parked owns it now.
        await _realtime.JobStateChangedAsync(job);
        await _realtime.ProjectionChangedAsync(job);
        return true;
    }

    /// <summary>
    /// Throws away the tracked-but-unsaved edits of an aborted release (refreshed snapshots, the
    /// half-set state) by re-reading the committed rows. Cheaper and far safer than clearing the
    /// change tracker, which would detach the other jobs this pass still has to work on.
    /// </summary>
    private async Task DiscardPendingEditsAsync(OperationJob job, CancellationToken ct)
    {
        await _db.Entry(job).ReloadAsync(ct);
        foreach (var item in job.Items)
            await _db.Entry(item).ReloadAsync(ct);
    }

    /// <summary>
    /// The job stays blocked, but possibly for a different reason than before — the target came
    /// back while the source is still missing, or a remount revealed the drive is now too full.
    /// Persisted only when something actually changed, so a quiet revaluation writes nothing.
    /// </summary>
    private async Task KeepBlockedAsync(
        OperationJob job, JobBlockReason reason, string message, int? dependsOnJobId, CancellationToken ct)
    {
        if (job.BlockReason == reason && job.ErrorMessage == message &&
            job.DependsOnJobId == dependsOnJobId)
        {
            return;
        }

        _logger.LogInformation(
            "Job {Id} stays blocked: {Old} → {New}.", job.Id, job.BlockReason, reason);

        job.BlockReason = reason;
        job.ErrorMessage = message;
        job.DependsOnJobId = dependsOnJobId;
        if (await SaveOrFollowConcurrentStateAsync(job, ct))
        {
            // Still Blocked, but for a different reason — the Coda shows the reason, so this is a
            // visible change. No ProjectionChanged: a Blocked job keeps its overlay (§5).
            await _realtime.JobStateChangedAsync(job);
        }
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
