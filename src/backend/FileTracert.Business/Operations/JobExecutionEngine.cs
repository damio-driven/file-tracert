using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
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
    /// <summary>
    /// Minimum wall-clock gap between two <c>BytesCopied</c>/<c>BytesProcessed</c> persists
    /// during a single file's copy. Bounds DB writes to ~1/sec regardless of file size or
    /// buffer count, instead of either 0 (silent until the whole item finishes) or one
    /// write per 80 KB buffer (thousands of writes on a multi-GB file).
    /// </summary>
    private static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(1);

    private readonly FileTracertDbContext _db;
    private readonly IFileMover _mover;
    private readonly ISpaceLedger _ledger;
    private readonly IndexUpdater _indexUpdater;
    private readonly INotificationPublisher _notifications;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobExecutionEngine> _logger;

    public JobExecutionEngine(
        FileTracertDbContext db,
        IFileMover mover,
        ISpaceLedger ledger,
        IndexUpdater indexUpdater,
        INotificationPublisher notifications,
        TimeProvider timeProvider,
        ILogger<JobExecutionEngine> logger)
    {
        _db = db;
        _mover = mover;
        _ledger = ledger;
        _indexUpdater = indexUpdater;
        _notifications = notifications;
        _timeProvider = timeProvider;
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
        catch (OperationCanceledException)
        {
            // Distinguish a user Cancel (job committed to Cancelled by the API) from a shutdown
            // (job stays runnable and resumes next start). On a real cancel, clean the orphan
            // .fadit-partial and swallow — this is expected, not an error. On shutdown, rethrow
            // so the worker loop stops and the job resumes later.
            if (await IsCancelledInDbAsync(job.Id))
            {
                _logger.LogInformation("Job {Id}: cancelled during execution — cleaning partials.", job.Id);
                await CleanupPartialsAsync(job);
                return;
            }
            throw;
        }
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
            // Exclude this job's own enqueue reservation and anything enqueued after it:
            // the re-check answers "does it fit NOW, given what still precedes me in FIFO".
            // HARD view (includeQueuedLiberations: false): a liberation promised by a preceding
            // job that has not completed yet is not physical space — never copy on its strength.
            var feasibility = await _ledger.ComputeFeasibilityAsync(
                tgtVol.Id, tgtVol.FreeBytesLastKnown, tgtVol.IsOnline, job.RequiredBytesTarget,
                excludeJobId: job.Id, sequenceOrder: job.SequenceOrder,
                includeQueuedLiberations: false, ct);

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
            // A Cancel may have committed (from the API's DbContext) without cancelling our token
            // — re-read before advancing so we don't march on to the destructive steps.
            if (await AbortIfCancelledAsync(job)) return;
            // Verified counts as "copy complete": a retried job can carry items already
            // finalized by the previous attempt — they must not hold the gate forever.
            if (job.Items.All(i => i.State is JobItemState.Copied or JobItemState.Verified or JobItemState.Done))
                await TransitionAsync(job, JobState.Verifying, ct);
        }

        if (job.State == JobState.Verifying)
        {
            if (await AbortIfCancelledAsync(job)) return;
            await VerifyAndFinalizeItemsAsync(job, ct);
            if (ct.IsCancellationRequested) return;
            // If VerifyAndFinalize set Failed/Blocked, abort.
            if (job.State is JobState.Failed or JobState.Blocked) return;
            // Re-check BEFORE the transition: TransitionAsync would otherwise overwrite a
            // concurrently-written Cancelled with DeletingSource and hide the cancel.
            if (await AbortIfCancelledAsync(job)) return;
            await TransitionAsync(job, JobState.DeletingSource, ct);
        }

        if (job.State == JobState.DeletingSource)
        {
            // Last line of defense before recycling the source: honour a concurrent cancel.
            if (await AbortIfCancelledAsync(job)) return;
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

        // Resume safety: an item left in Copying by an interrupted prior run (crash / cancel)
        // has an orphan .fadit-partial and would be skipped by the Pending filter below —
        // the job would then never satisfy the "all copied" gate and stall forever.
        // Reset those items to Pending so they are re-copied from scratch; the partial is
        // discardable and gets overwritten (CopyFileAsync opens it with FileMode.Create).
        var interrupted = job.Items.Where(i => i.State == JobItemState.Copying).ToList();
        if (interrupted.Count > 0)
        {
            foreach (var item in interrupted)
            {
                item.State = JobItemState.Pending;
                item.BytesCopied = 0;
            }
            _logger.LogInformation(
                "Job {Id}: reset {Count} interrupted item(s) to Pending for re-copy.", job.Id, interrupted.Count);
        }

        // Idempotent progress: rebuild the job counter from the items' real states instead of
        // trusting the persisted accumulator. A pre-crash live tick may have counted partial
        // bytes of an item that was just reset above — keeping them and re-copying the item
        // would double-count and push the progress past 100%.
        job.BytesProcessed = CompletedItemBytes(job);
        await _db.SaveChangesAsync(ct);

        foreach (var item in job.Items.Where(i => i.State == JobItemState.Pending))
        {
            ct.ThrowIfCancellationRequested();

            // Ensure destination directory exists.
            var dstDir = ScanPath.Parent(item.TargetRelativePath);
            if (!string.IsNullOrEmpty(dstDir))
                _mover.EnsureTargetDirectory(tgtGuid, dstDir);

            var partialRel = item.TargetRelativePath + ".fadit-partial";
            item.TempPath = partialRel;
            item.State = JobItemState.Copying;
            await _db.SaveChangesAsync(ct);

            // Baseline = bytes of items already completed (derived from their states, never the
            // raw accumulator); this item's live BytesCopied goes on top for a job-level total
            // that updates during the copy, not only when the whole item completes.
            var bytesProcessedBeforeThisItem = CompletedItemBytes(job);
            var lastSaveTimestamp = _timeProvider.GetTimestamp();

            async Task PersistProgressAsync(long bytesCopied, CancellationToken tickCt)
            {
                item.BytesCopied = bytesCopied;
                if (_timeProvider.GetElapsedTime(lastSaveTimestamp) < ProgressSaveInterval)
                    return;
                lastSaveTimestamp = _timeProvider.GetTimestamp();
                job.BytesProcessed = bytesProcessedBeforeThisItem + bytesCopied;
                await _db.SaveChangesAsync(tickCt);
            }

            await _mover.CopyFileAsync(srcGuid, item.SourceRelativePath, tgtGuid, partialRel, PersistProgressAsync, ct);

            item.BytesCopied = item.SizeBytes;
            item.State = JobItemState.Copied;
            job.BytesProcessed = CompletedItemBytes(job);
            await _db.SaveChangesAsync(ct);

            _logger.LogDebug("Job {Id}: copied '{Src}'.", job.Id, item.SourceRelativePath);
        }
    }

    /// <summary>
    /// Bytes of items whose copy is complete, derived from item states. The single source of
    /// truth for <see cref="OperationJob.BytesProcessed"/>: recomputing (instead of accumulating)
    /// keeps the progress idempotent across crash/resume — 100% means 100%.
    /// </summary>
    private static long CompletedItemBytes(OperationJob job) =>
        job.Items
            .Where(i => i.State is JobItemState.Copied or JobItemState.Verified or JobItemState.Done)
            .Sum(i => i.SizeBytes);

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
        // Nothing below this line is reversible — bail out if cancellation raced in.
        ct.ThrowIfCancellationRequested();

        var srcGuid = job.SourceVolume!.VolumeGuid;

        if (job.Type == JobType.MoveFile)
        {
            // FirstOrDefault: on a resume/retry the single item may already be Done.
            var item = job.Items.FirstOrDefault(i => i.State == JobItemState.Verified);
            if (item is not null)
            {
                _mover.DeleteToRecycleBin(srcGuid, item.SourceRelativePath);
                item.State = JobItemState.Done;
            }
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

            await _db.SaveChangesAsync(ct);

            // Then recycle the source directory subtree. The old code picked the SHORTEST item
            // DirPath as "the root" and recycled only that — when the files live in deep subfolders
            // that shortest path is a leaf subfolder, so the real root and every intermediate dir
            // survived. Model the whole subtree explicitly (as the copy half does per item) and
            // recycle deepest-first so each directory is emptied before its parent.
            await DeleteSourceSubtreeAsync(job, srcGuid, ct);
            return;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Recycles the moved folder's entire source subtree, deepest directory first. The subtree is
    /// read from the still-source-shaped <c>Directories</c> rows (IndexUpdater re-parents only after
    /// completion), guaranteeing the root and empty intermediates are removed, not just the
    /// directories that happened to hold indexed files.
    /// </summary>
    private async Task DeleteSourceSubtreeAsync(OperationJob job, string srcGuid, CancellationToken ct)
    {
        var srcRoot = ResolveSourceRoot(job);
        if (string.IsNullOrEmpty(srcRoot) || job.SourceVolumeId is null)
            return;

        var prefixWithSep = srcRoot + "\\";
        var dirPaths = await _db.Directories.AsNoTracking()
            .Where(d => d.VolumeId == job.SourceVolumeId.Value &&
                        (d.MaterializedPath == srcRoot || d.MaterializedPath.StartsWith(prefixWithSep)))
            .Select(d => d.MaterializedPath)
            .ToListAsync(ct);

        // Deepest-first: order by segment depth so a child is always recycled before its parent.
        foreach (var dirPath in dirPaths.OrderByDescending(p => p.Count(c => c == '\\')).ThenByDescending(p => p.Length))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _mover.DeleteToRecycleBin(srcGuid, dirPath);
            }
            catch (Exception ex)
            {
                // Not silent (§9): the files are already safely on the target, this is best-effort
                // subtree cleanup — log the full exception and continue with the remaining dirs.
                _logger.LogWarning(ex, "Job {Id}: could not recycle source directory '{Dir}'.", job.Id, dirPath);
            }
        }
    }

    /// <summary>
    /// Reconstructs the moved folder's original root path. Source and target of every item share
    /// the same tail below their respective roots (<c>srcRoot\tail</c> ↔ <c>TargetRelativePath\tail</c>),
    /// so stripping that tail off one item's source yields the root — independent of how deep the
    /// files sit.
    /// </summary>
    private static string ResolveSourceRoot(OperationJob job)
    {
        var dst = job.TargetRelativePath;
        var item = job.Items.FirstOrDefault(i => i.FileId.HasValue);
        if (item is null || string.IsNullOrEmpty(dst))
            return string.Empty;

        var prefix = dst + "\\";
        if (item.TargetRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var tail = item.TargetRelativePath[prefix.Length..];      // "relwithin\name" or "name"
            var suffix = "\\" + tail;
            if (item.SourceRelativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return item.SourceRelativePath[..^suffix.Length];
        }

        // Fallback: the file sits directly under the moved folder → root is its parent dir.
        return ScanPath.Parent(item.SourceRelativePath);
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

        // Fold the job's now-materialized space effect into the volumes' last-known free bytes
        // BEFORE releasing its ledger entries: releasing alone would make the target look
        // roomier (reservation gone, stale free unchanged) and the source liberation would
        // vanish without ever reaching FreeBytesLastKnown — blocking jobs that now fit until
        // the next volume probe. Estimate bookkeeping only; the periodic sync overwrites it.
        if (!job.IsIntraVolume)
        {
            if (job.TargetVolume is not null && job.RequiredBytesTarget > 0)
                job.TargetVolume.FreeBytesLastKnown =
                    Math.Max(0, job.TargetVolume.FreeBytesLastKnown - job.RequiredBytesTarget);
            if (job.SourceVolume is not null && job.FreedBytesSource > 0)
                job.SourceVolume.FreeBytesLastKnown += job.FreedBytesSource;
        }

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

        // The engine runs in a BackgroundService: no API response can carry this to the
        // user, so a block on a user-queued operation must land in Notifications (§9).
        await _notifications.PublishAsync(
            NotificationSeverity.Warning,
            "Coda",
            $"Operazione {job.Type} bloccata ({reason})",
            message,
            job.TargetVolumeId ?? job.SourceVolumeId,
            ct);
    }

    private async Task SetFailedAsync(OperationJob job, string message, CancellationToken ct)
    {
        job.State = JobState.Failed;
        job.ErrorMessage = message;
        job.CompletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _ledger.ReleaseAsync(job.Id, ct);

        // FIX #10-partial: a Failed job's .fadit-partial files are discardable garbage
        // (a retry re-copies from scratch) — never leave them on the target.
        await CleanupPartialsAsync(job);

        _logger.LogError("Job {Id} failed: {Msg}.", job.Id, message);

        // Resilience, not silence: the processor moves on to the next job, but the
        // failure of a user-queued operation must be visible in the UI bell.
        await _notifications.PublishAsync(
            NotificationSeverity.Error,
            "Coda",
            $"Operazione {job.Type} fallita",
            message,
            job.TargetVolumeId ?? job.SourceVolumeId,
            ct);
    }

    // ── cancellation guards ───────────────────────────────────────────────────

    /// <summary>
    /// Re-reads the job's committed <see cref="JobState"/> from the database. A concurrent
    /// <c>CancelAsync</c> writes <c>Cancelled</c> on a DIFFERENT DbContext, so our tracked
    /// entity never sees it — a projection query hits the DB and returns the true value.
    /// Uses <see cref="CancellationToken.None"/> so the read completes even when the job token
    /// is already tripped.
    /// </summary>
    private async Task<bool> IsCancelledInDbAsync(int jobId)
    {
        var state = await _db.OperationJobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.State)
            .FirstOrDefaultAsync(CancellationToken.None);
        return state == JobState.Cancelled;
    }

    /// <summary>
    /// If the job was cancelled, cleans up any orphan <c>.fadit-partial</c> and returns true so
    /// the caller aborts before the next (possibly destructive) step. The source is never touched.
    /// </summary>
    private async Task<bool> AbortIfCancelledAsync(OperationJob job)
    {
        if (!await IsCancelledInDbAsync(job.Id))
            return false;

        _logger.LogInformation(
            "Job {Id}: cancellation detected — aborting before the next step; source left untouched.", job.Id);
        await CleanupPartialsAsync(job);
        return true;
    }

    /// <summary>
    /// Deletes every item's <c>.fadit-partial</c> from the target and clears its
    /// <c>TempPath</c> pointer (persisted with <see cref="CancellationToken.None"/> —
    /// cleanup runs on failure/cancel paths where the job token may already be tripped).
    /// A partial that cannot be removed keeps its TempPath so a later pass can retry.
    /// </summary>
    private async Task CleanupPartialsAsync(OperationJob job)
    {
        var tgtGuid = job.TargetVolume?.VolumeGuid;
        if (tgtGuid is null) return;

        bool anyCleared = false;
        foreach (var item in job.Items.Where(i => !string.IsNullOrEmpty(i.TempPath)))
        {
            try
            {
                // A partial that never hit the disk (copy aborted before creating it) is
                // already clean — recycling a missing path would throw and leave the pointer.
                if (_mover.Exists(tgtGuid, item.TempPath!))
                    _mover.DeleteToRecycleBin(tgtGuid, item.TempPath!);
                item.TempPath = null;
                anyCleared = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Job {Id}: could not remove orphan partial '{Path}'.", job.Id, item.TempPath);
            }
        }

        if (anyCleared)
            await _db.SaveChangesAsync(CancellationToken.None);
    }
}
