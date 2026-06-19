using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Executes a single <see cref="OperationJob"/> to completion, advancing the state machine
/// checkpoint by checkpoint and persisting each transition so the worker can resume on restart.
///
/// Intra-volume ops (rename / move) and CreateFolder are atomic from the OS perspective
/// — single call, then Completed. Cross-volume moves go through the full
/// Pending → SpaceReserved → Copying → Verifying → DeletingSource → Completed pipeline.
/// </summary>
public sealed class JobExecutionEngine
{
    private readonly FileTracertDbContext _db;
    private readonly IFileMover _mover;
    private readonly ISpaceLedger _ledger;
    private readonly IndexUpdater _indexUpdater;
    private readonly ILogger<JobExecutionEngine> _logger;

    public JobExecutionEngine(
        FileTracertDbContext db,
        IFileMover mover,
        ISpaceLedger ledger,
        IndexUpdater indexUpdater,
        ILogger<JobExecutionEngine> logger)
    {
        _db = db;
        _mover = mover;
        _ledger = ledger;
        _indexUpdater = indexUpdater;
        _logger = logger;
    }

    public async Task ExecuteJobAsync(int jobId, CancellationToken ct)
    {
        var job = await _db.OperationJobs
            .Include(j => j.Items)
            .Include(j => j.SourceVolume)
            .Include(j => j.TargetVolume)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null)
        {
            _logger.LogWarning("JobExecutionEngine: job {Id} not found.", jobId);
            return;
        }

        _logger.LogInformation("Executing job {Id} type={Type} state={State}.", job.Id, job.Type, job.State);

        try
        {
            bool simple = job.IsIntraVolume ||
                          job.Type is JobType.CreateFolder or JobType.RenameFile or JobType.RenameFolder;

            if (simple)
                await ExecuteSimpleAsync(job, ct);
            else
                await ExecuteCrossVolumeAsync(job, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (NameCollisionException ex)
        {
            _logger.LogWarning("Job {Id}: name collision at '{Path}'.", job.Id, ex.TargetPath);
            await SetBlockedAsync(job, JobBlockReason.NameCollision, ex.Message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Id} failed during execution.", job.Id);
            await SetFailedAsync(job, ex.Message, ct);
        }
    }

    // ── simple ops (one OS call → Completed) ─────────────────────────────────

    private async Task ExecuteSimpleAsync(OperationJob job, CancellationToken ct)
    {
        MarkStarted(job);
        await _db.SaveChangesAsync(ct);

        var srcGuid = job.SourceVolume?.VolumeGuid;
        var tgtGuid = job.TargetVolume?.VolumeGuid ?? srcGuid;
        var item = job.Items.FirstOrDefault();

        switch (job.Type)
        {
            case JobType.CreateFolder:
                _mover.CreateFolder(tgtGuid!, job.TargetRelativePath!);
                break;

            case JobType.RenameFile:
            case JobType.RenameFolder:
                _mover.RenameIntraVolume(srcGuid!, item!.SourceRelativePath, job.TargetRelativePath!);
                break;

            case JobType.MoveFile:
            case JobType.MoveFolder:
                _mover.MoveIntraVolume(srcGuid!, item!.SourceRelativePath, item.TargetRelativePath);
                break;
        }

        if (item is not null)
        {
            item.State = JobItemState.Done;
        }

        await CompleteJobAsync(job, ct);
        await _indexUpdater.UpdateAfterCompletionAsync(job, ct);
    }

    // ── cross-volume state machine ────────────────────────────────────────────

    private async Task ExecuteCrossVolumeAsync(OperationJob job, CancellationToken ct)
    {
        MarkStarted(job);

        if (job.State == JobState.Pending)
        {
            // Hard space check before we commit to copying.
            var tgtVol = job.TargetVolume!;
            var feasibility = await _ledger.ComputeFeasibilityAsync(
                tgtVol.Id, tgtVol.FreeBytesLastKnown, tgtVol.IsOnline, job.RequiredBytesTarget, ct);

            if (!feasibility.Feasible)
            {
                _logger.LogWarning("Job {Id}: insufficient space at execution time. Deficit={D}.", job.Id, feasibility.DeficitBytes);
                await SetBlockedAsync(job, JobBlockReason.InsufficientSpace,
                    $"Insufficient space: {feasibility.DeficitBytes} bytes short on volume {tgtVol.Id}.", ct);
                return;
            }

            await TransitionAsync(job, JobState.SpaceReserved, ct);
        }

        if (job.State == JobState.SpaceReserved)
            await TransitionAsync(job, JobState.Copying, ct);

        if (job.State == JobState.Copying)
        {
            await CopyItemsAsync(job, ct);
            // Cancelled mid-copy: leave in Copying so the next run resumes.
            if (ct.IsCancellationRequested) return;
            if (job.Items.All(i => i.State is JobItemState.Copied or JobItemState.Done))
                await TransitionAsync(job, JobState.Verifying, ct);
        }

        if (job.State == JobState.Verifying)
        {
            await VerifyAndFinalizeItemsAsync(job, ct);
            if (ct.IsCancellationRequested) return;
            // If VerifyAndFinalize set Failed/Blocked, abort.
            if (job.State is JobState.Failed or JobState.Blocked) return;
            await TransitionAsync(job, JobState.DeletingSource, ct);
        }

        if (job.State == JobState.DeletingSource)
        {
            await DeleteSourcesAsync(job, ct);
            await CompleteJobAsync(job, ct);
            await _indexUpdater.UpdateAfterCompletionAsync(job, ct);
        }
    }

    // ── copy phase ────────────────────────────────────────────────────────────

    private async Task CopyItemsAsync(OperationJob job, CancellationToken ct)
    {
        var srcGuid = job.SourceVolume!.VolumeGuid;
        var tgtGuid = job.TargetVolume!.VolumeGuid;

        foreach (var item in job.Items.Where(i => i.State == JobItemState.Pending))
        {
            ct.ThrowIfCancellationRequested();

            // Ensure destination directory exists.
            var dstDir = DirPath(item.TargetRelativePath);
            if (!string.IsNullOrEmpty(dstDir))
                _mover.EnsureTargetDirectory(tgtGuid, dstDir);

            var partialRel = item.TargetRelativePath + ".fadit-partial";
            item.TempPath = partialRel;
            item.State = JobItemState.Copying;
            await _db.SaveChangesAsync(ct);

            // IProgress tick: update BytesCopied in memory (no DB write per tick to avoid thrashing).
            var progress = new Progress<long>(bytes => item.BytesCopied = bytes);

            await _mover.CopyFileAsync(srcGuid, item.SourceRelativePath, tgtGuid, partialRel, progress, ct);

            item.BytesCopied = item.SizeBytes;
            item.State = JobItemState.Copied;
            job.BytesProcessed += item.SizeBytes;
            await _db.SaveChangesAsync(ct);

            _logger.LogDebug("Job {Id}: copied '{Src}'.", job.Id, item.SourceRelativePath);
        }
    }

    // ── verify + finalize phase ───────────────────────────────────────────────

    private async Task VerifyAndFinalizeItemsAsync(OperationJob job, CancellationToken ct)
    {
        var srcGuid = job.SourceVolume!.VolumeGuid;
        var tgtGuid = job.TargetVolume!.VolumeGuid;

        foreach (var item in job.Items.Where(i => i.State == JobItemState.Copied))
        {
            ct.ThrowIfCancellationRequested();

            var partialRel = item.TempPath!;

            // Size-only verification (full hash is optional, not enabled for MVP).
            bool ok = _mover.Verify(srcGuid, item.SourceRelativePath, tgtGuid, partialRel, withHash: false);
            if (!ok)
            {
                _logger.LogError("Job {Id}: verification failed for '{Src}'.", job.Id, item.SourceRelativePath);
                item.State = JobItemState.Failed;
                item.ErrorMessage = "Size mismatch after copy.";
                await _db.SaveChangesAsync(ct);
                await SetFailedAsync(job, "Verification failed on one or more items.", ct);
                return;
            }

            // Rename .fadit-partial → final. Throws NameCollisionException if final already exists.
            _mover.FinalizePartial(tgtGuid, partialRel, item.TargetRelativePath);

            item.TempPath = null;
            item.State = JobItemState.Verified;
            await _db.SaveChangesAsync(ct);

            _logger.LogDebug("Job {Id}: finalized '{Dst}'.", job.Id, item.TargetRelativePath);
        }
    }

    // ── delete source phase ───────────────────────────────────────────────────

    private async Task DeleteSourcesAsync(OperationJob job, CancellationToken ct)
    {
        var srcGuid = job.SourceVolume!.VolumeGuid;

        if (job.Type == JobType.MoveFile)
        {
            var item = job.Items.First(i => i.State == JobItemState.Verified);
            _mover.DeleteToRecycleBin(srcGuid, item.SourceRelativePath);
            item.State = JobItemState.Done;
        }
        else
        {
            // MoveFolder cross-volume: delete individual source files first.
            foreach (var item in job.Items.Where(i => i.State == JobItemState.Verified))
            {
                ct.ThrowIfCancellationRequested();
                _mover.DeleteToRecycleBin(srcGuid, item.SourceRelativePath);
                item.State = JobItemState.Done;
            }

            // Then delete the (now-empty) source directory tree by finding the common root.
            var srcTopDir = job.Items
                .Select(i => DirPath(i.SourceRelativePath))
                .Where(d => !string.IsNullOrEmpty(d))
                .OrderBy(d => d.Length)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(srcTopDir))
            {
                try { _mover.DeleteToRecycleBin(srcGuid, srcTopDir); }
                catch (Exception ex)
                {
                    // Non-fatal: the directory might not be empty (non-indexed files) or already gone.
                    _logger.LogWarning(ex, "Job {Id}: could not delete source directory '{Dir}'.", job.Id, srcTopDir);
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    // ── state machine helpers ─────────────────────────────────────────────────

    private void MarkStarted(OperationJob job)
    {
        if (job.StartedUtc is null)
            job.StartedUtc = DateTime.UtcNow;
    }

    private async Task TransitionAsync(OperationJob job, JobState newState, CancellationToken ct)
    {
        job.State = newState;
        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("Job {Id}: → {State}.", job.Id, newState);
    }

    private async Task CompleteJobAsync(OperationJob job, CancellationToken ct)
    {
        job.State = JobState.Completed;
        job.CompletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _ledger.ReleaseAsync(job.Id, ct);
        _logger.LogInformation("Job {Id} completed.", job.Id);
    }

    private async Task SetBlockedAsync(OperationJob job, JobBlockReason reason, string message, CancellationToken ct)
    {
        job.State = JobState.Blocked;
        job.BlockReason = reason;
        job.ErrorMessage = message;
        await _db.SaveChangesAsync(ct);
        // Ledger reservation kept — the job may still execute once the blocker resolves.
    }

    private async Task SetFailedAsync(OperationJob job, string message, CancellationToken ct)
    {
        job.State = JobState.Failed;
        job.ErrorMessage = message;
        job.CompletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _ledger.ReleaseAsync(job.Id, ct);
        _logger.LogError("Job {Id} failed: {Msg}.", job.Id, message);
    }

    // ── path utilities ────────────────────────────────────────────────────────

    private static string DirPath(string path)
    {
        var idx = path.LastIndexOf('\\');
        return idx < 0 ? string.Empty : path[..idx];
    }
}
