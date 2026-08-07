using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Re-evaluates jobs parked in <c>Blocked(InsufficientSpace)</c> and returns them to
/// <c>Pending</c> when the space they need has become real. The queue worker runs this
/// after every completed job (§4: Blocked jobs are re-evaluated on every event), which is
/// what makes a job blocked behind a space-freeing predecessor restart on its own.
///
/// Uses the HARD feasibility view (no credit for promised liberations of jobs that have
/// not completed) so an unblocked job is guaranteed to pass the engine's execution-time
/// re-check instead of ping-ponging Blocked → Pending → Blocked.
/// </summary>
public sealed class BlockedJobRevaluator
{
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
            .Include(j => j.TargetVolume)
            .Where(j => j.State == JobState.Blocked &&
                        j.BlockReason == JobBlockReason.InsufficientSpace &&
                        j.TargetVolumeId != null)
            .OrderBy(j => j.SequenceOrder)
            .ToListAsync(ct);

        int unblockedCount = 0;

        // FIFO order matters: a job unblocked here re-enters the ledger demand that the
        // feasibility of the next candidate must account for.
        foreach (var job in blocked)
        {
            var tgtVol = job.TargetVolume!;
            var feasibility = await _ledger.ComputeFeasibilityAsync(
                tgtVol.Id, tgtVol.FreeBytesLastKnown, tgtVol.IsOnline, job.RequiredBytesTarget,
                excludeJobId: job.Id, sequenceOrder: job.SequenceOrder,
                includeQueuedLiberations: false, ct);

            if (!feasibility.Feasible)
                continue;

            job.State = JobState.Pending;
            job.BlockReason = JobBlockReason.None;
            job.ErrorMessage = null;
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A cancel raced the unblock: keep the committed state, skip this job and
                // keep the revaluation pass alive for the remaining candidates.
                await _db.Entry(job).ReloadAsync(ct);
                _logger.LogInformation(
                    "Job {Id}: state moved concurrently during revaluation (now {State}) — skipped.",
                    job.Id, job.State);
                continue;
            }

            // Guarantee exactly one active reservation: a job blocked at enqueue never
            // reserved (shouldReserve was false), one blocked by the engine kept its
            // reservation — release-then-reserve normalizes both cases.
            if (!job.IsIntraVolume && job.RequiredBytesTarget > 0)
            {
                await _ledger.ReleaseAsync(job.Id, ct);
                await _ledger.ReserveAsync(
                    job.Id, job.SequenceOrder, job.TargetVolumeId!.Value,
                    job.RequiredBytesTarget, job.SourceVolumeId, job.FreedBytesSource, ct);
            }

            unblockedCount++;
            _logger.LogInformation(
                "Job {Id} unblocked: space now available on volume {Vol} (was InsufficientSpace).",
                job.Id, tgtVol.Id);
        }

        return unblockedCount;
    }
}
