using FileTracert.Business.Projection;
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
    private readonly OverlayWriter _overlay;
    private readonly INotificationPublisher _notifications;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobExecutionEngine> _logger;

    public JobExecutionEngine(
        FileTracertDbContext db,
        IFileMover mover,
        ISpaceLedger ledger,
        IndexUpdater indexUpdater,
        OverlayWriter overlay,
        INotificationPublisher notifications,
        TimeProvider timeProvider,
        ILogger<JobExecutionEngine> logger)
    {
        _db = db;
        _mover = mover;
        _ledger = ledger;
        _indexUpdater = indexUpdater;
        _overlay = overlay;
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
            // FIX #3 — offline gate. Checked HERE, immediately before any syscall, and not only at
            // enqueue: a volume can disappear while the job sits in the queue or between two of its
            // checkpoints. A parked job keeps its ledger reservation (see SetBlockedAsync) so the
            // space it will need at the remount stays committed to it.
            var offline = VolumeOfflineGate.Evaluate(job.SourceVolume, job.TargetVolume);
            if (offline != JobBlockReason.None)
            {
                _logger.LogInformation(
                    "Job {Id}: not attempted — {Reason}; parked until the volume comes back.", job.Id, offline);
                await SetBlockedAsync(job, offline,
                    VolumeOfflineGate.Describe(offline, job.SourceVolume, job.TargetVolume), ct);
                return;
            }

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
                // FIX #14: items already landed on the target keep their finalized copy indexed.
                await _indexUpdater.ReconcileCancelledJobAsync(job, CancellationToken.None);
                return;
            }
            throw;
        }
        catch (NameCollisionException ex)
        {
            _logger.LogWarning("Job {Id}: name collision at '{Path}'.", job.Id, ex.TargetPath);
            await SetBlockedAsync(job, JobBlockReason.NameCollision, ex.Message, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The State concurrency token tripped: another DbContext committed a transition
            // (in practice a user Cancel from the API) between our last read and this write.
            // Never overwrite it — follow the committed state instead.
            await HandleConcurrentStateChangeAsync(job);
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
                _mover.CreateFolder(tgtGuid!, job.TargetRelativePath!); // mkdir is idempotent
                break;

            case JobType.RenameFile:
            case JobType.RenameFolder:
                if (!SimpleOpAlreadyApplied(job, srcGuid!, item!))
                    _mover.RenameIntraVolume(srcGuid!, item!.SourceRelativePath, job.TargetRelativePath!);
                break;

            case JobType.MoveFile:
            case JobType.MoveFolder:
                if (!SimpleOpAlreadyApplied(job, srcGuid!, item!))
                    _mover.MoveIntraVolume(srcGuid!, item!.SourceRelativePath, item.TargetRelativePath);
                break;
        }

        if (item is not null)
        {
            item.State = JobItemState.Done;
        }

        await CompleteJobAsync(job, ct);
    }

    /// <summary>
    /// Crash-resume guard for the single-OS-call ops (finding #4): an intra-volume rename/move
    /// has no checkpoint between the call and Completed, so after a crash the re-run must
    /// recognize "target exists + source absent" as already applied — re-executing would throw
    /// FileNotFoundException and fail an operation that physically succeeded (and the index
    /// update, which runs after completion, would never happen).
    /// </summary>
    private bool SimpleOpAlreadyApplied(OperationJob job, string volGuid, OperationJobItem item)
    {
        bool applied = _mover.Exists(volGuid, item.TargetRelativePath) &&
                       !_mover.Exists(volGuid, item.SourceRelativePath);
        if (applied)
            _logger.LogInformation(
                "Job {Id}: '{Target}' already in place and source absent — applied by an interrupted run; completing.",
                job.Id, item.TargetRelativePath);
        return applied;
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
            // Verified counts as "copy complete": a retried job can carry items already
            // finalized by the previous attempt — they must not hold the gate forever.
            // A Cancel committed meanwhile trips the State concurrency token here.
            if (job.Items.All(i => i.State is JobItemState.Copied or JobItemState.Verified or JobItemState.Done))
                await TransitionAsync(job, JobState.Verifying, ct);
        }

        if (job.State == JobState.Verifying)
        {
            // Finalize publishes files under their real name — honour a concurrent Cancel
            // before doing so (the token alone would only trip at the NEXT DB write).
            if (await AbortIfCancelledAsync(job)) return;
            await VerifyAndFinalizeItemsAsync(job, ct);
            if (ct.IsCancellationRequested) return;
            // If VerifyAndFinalize set Failed/Blocked, abort.
            if (job.State is JobState.Failed or JobState.Blocked) return;
            // A Cancel committed after the guard above trips the token on this transition.
            await TransitionAsync(job, JobState.DeletingSource, ct);
        }

        if (job.State == JobState.DeletingSource)
        {
            // Last line of defense before recycling the source: honour a concurrent cancel.
            if (await AbortIfCancelledAsync(job)) return;
            // #15: CompleteJobAsync passes the physically removed source directories to the
            // index update so exactly those rows are dropped — surviving ones (uncopied
            // leftovers) stay navigable.
            var removedSourceDirs = await DeleteSourcesAsync(job, ct);
            await CompleteJobAsync(job, removedSourceDirs, ct);
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

        // Folder marker (MoveFolder cross-volume, FileId = null): the folder itself moves,
        // not just its files. Materialize the whole target directory tree — including empty
        // subdirectories — so an empty or all-excluded folder still produces its destination
        // (C21) and structure is never lost. CreateFolder is idempotent, safe on resume.
        var marker = FindFolderMarker(job, pendingOnly: true);
        if (marker is not null)
        {
            _mover.CreateFolder(tgtGuid, marker.TargetRelativePath);
            await EnsureTargetSubtreeAsync(job, marker, tgtGuid, ct);
            marker.State = JobItemState.Done;
            await _db.SaveChangesAsync(ct);
        }

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
    /// Creates on the target every directory of the moved folder's source subtree, mapped
    /// under the destination root. Read from the Directories rows (still source-shaped at
    /// this point), so empty subdirectories — which the per-file expansion cannot see —
    /// are recreated too.
    /// </summary>
    private async Task EnsureTargetSubtreeAsync(
        OperationJob job, OperationJobItem marker, string tgtGuid, CancellationToken ct)
    {
        if (job.SourceVolumeId is null) return;

        var srcRoot = marker.SourceRelativePath;
        var subDirPaths = await _db.Directories.AsNoTracking()
            .InSubtree(job.SourceVolumeId.Value, srcRoot, includeRoot: false)
            .Select(d => d.MaterializedPath)
            .ToListAsync(ct);

        foreach (var srcDirPath in subDirPaths)
        {
            ct.ThrowIfCancellationRequested();
            _mover.CreateFolder(tgtGuid, marker.TargetRelativePath + srcDirPath[srcRoot.Length..]);
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

            // Crash-resume (finding #4): FinalizePartial can have renamed partial→final with the
            // process dying before Verified was persisted. "final exists + partial absent" is that
            // exact footprint — verify the final in place instead of failing on the missing partial
            // (and instead of re-copying into a NameCollision against our own output on retry).
            // Known limit: with hash off (MVP) a FOREIGN same-size file at the final path passes
            // the in-place verify and gets adopted; hash verification closes this when enabled.
            bool alreadyFinalized = !_mover.Exists(tgtGuid, partialRel) &&
                                    _mover.Exists(tgtGuid, item.TargetRelativePath);
            if (alreadyFinalized)
                _logger.LogInformation(
                    "Job {Id}: '{Dst}' already finalized by an interrupted run — verifying in place.",
                    job.Id, item.TargetRelativePath);

            var candidateRel = alreadyFinalized ? item.TargetRelativePath : partialRel;

            // Size-only verification (full hash is optional, not enabled for MVP).
            bool ok = _mover.Verify(srcGuid, item.SourceRelativePath, tgtGuid, candidateRel, withHash: false);
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
            if (!alreadyFinalized)
                _mover.FinalizePartial(tgtGuid, partialRel, item.TargetRelativePath);

            item.TempPath = null;
            item.State = JobItemState.Verified;
            await _db.SaveChangesAsync(ct);

            _logger.LogDebug("Job {Id}: finalized '{Dst}'.", job.Id, item.TargetRelativePath);
        }
    }

    // ── delete source phase ───────────────────────────────────────────────────

    /// <summary>Returns the source directory paths physically removed (MoveFolder only).</summary>
    private async Task<List<string>> DeleteSourcesAsync(OperationJob job, CancellationToken ct)
    {
        // Nothing below this line is reversible — bail out if cancellation raced in.
        ct.ThrowIfCancellationRequested();

        var srcGuid = job.SourceVolume!.VolumeGuid;

        // On volumes without a recycle bin (removable FAT/exFAT) FOF_ALLOWUNDO silently
        // degrades to a permanent delete. The data is already copied+verified on the target,
        // so the move proceeds — but the loss of undo must be surfaced, never silent (§9).
        if (!_mover.CanRecycle(srcGuid))
        {
            _logger.LogWarning(
                "Job {Id}: source volume {Vol} has no recycle bin — source files will be deleted permanently.",
                job.Id, job.SourceVolumeId);
            await _notifications.PublishAsync(
                NotificationSeverity.Warning,
                "Coda",
                "Volume di origine senza cestino",
                "Il volume di origine non ha un cestino: i file di origine verranno eliminati " +
                "definitivamente (copia già verificata sulla destinazione, ma nessun annullamento possibile).",
                job.SourceVolumeId,
                ct);
        }

        if (job.Type == JobType.MoveFile)
        {
            // FirstOrDefault: on a resume/retry the single item may already be Done.
            var item = job.Items.FirstOrDefault(i => i.State == JobItemState.Verified);
            if (item is not null)
            {
                RecycleSourceIfPresent(job, srcGuid, item);
                item.State = JobItemState.Done;
            }

            await _db.SaveChangesAsync(ct);
            return [];
        }

        // MoveFolder cross-volume: delete individual source files first. Checkpoint per item
        // (finding #4): a crash mid-loop must find the recycled items persisted as Done, not
        // re-recycle them (their target copy is verified — the source is legitimately gone).
        foreach (var item in job.Items.Where(i => i.State == JobItemState.Verified))
        {
            ct.ThrowIfCancellationRequested();
            RecycleSourceIfPresent(job, srcGuid, item);
            item.State = JobItemState.Done;
            await _db.SaveChangesAsync(ct);
        }

        // Then remove the source directory subtree (empty dirs only — see
        // DeleteSourceSubtreeAsync). The old code picked the SHORTEST item DirPath as
        // "the root" and recycled only that — when the files live in deep subfolders that
        // shortest path is a leaf subfolder, so the real root and every intermediate dir
        // survived. Model the whole subtree explicitly and go deepest-first so each
        // directory is emptied before its parent.
        return await DeleteSourceSubtreeAsync(job, srcGuid, ct);
    }

    /// <summary>
    /// Recycles an item's source file, tolerating a path already absent: after a crash mid
    /// DeletingSource the interrupted run may have recycled it without persisting Done
    /// (finding #4) — that item's work is done, not an error to re-raise.
    /// </summary>
    private void RecycleSourceIfPresent(OperationJob job, string srcGuid, OperationJobItem item)
    {
        if (_mover.Exists(srcGuid, item.SourceRelativePath))
        {
            _mover.DeleteToRecycleBin(srcGuid, item.SourceRelativePath);
            return;
        }

        _logger.LogInformation(
            "Job {Id}: source '{Src}' already absent — recycled by an interrupted run, treated as done.",
            job.Id, item.SourceRelativePath);
    }

    /// <summary>
    /// Removes the moved folder's source directories, deepest first, recycling a directory
    /// ONLY when it is empty. The expansion covers just the indexed+included files, so the
    /// physical subtree can hold content the job never copied (excluded files, files the
    /// scanner never saw): recycling the tree blindly would destroy data that exists nowhere
    /// else. A non-empty directory is left in place and the incompleteness is surfaced via
    /// log + Notification (§9). Returns the paths actually removed so the index can drop
    /// exactly those rows.
    /// </summary>
    private async Task<List<string>> DeleteSourceSubtreeAsync(OperationJob job, string srcGuid, CancellationToken ct)
    {
        var removed = new List<string>();
        var srcRoot = ResolveSourceRoot(job);
        if (string.IsNullOrEmpty(srcRoot) || job.SourceVolumeId is null)
            return removed;

        var dirPaths = await _db.Directories.AsNoTracking()
            .InSubtree(job.SourceVolumeId.Value, srcRoot)
            .Select(d => d.MaterializedPath)
            .ToListAsync(ct);

        var keptNonEmpty = new List<string>();

        // Deepest-first: order by segment depth so a child is always emptied and recycled
        // before its parent is examined.
        foreach (var dirPath in dirPaths.OrderByDescending(p => p.Count(c => c == '\\')).ThenByDescending(p => p.Length))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!_mover.Exists(srcGuid, dirPath))
                    continue;

                if (!_mover.IsDirectoryEmpty(srcGuid, dirPath))
                {
                    keptNonEmpty.Add(dirPath);
                    continue;
                }

                _mover.DeleteToRecycleBin(srcGuid, dirPath);
                removed.Add(dirPath);
            }
            catch (Exception ex)
            {
                // Not silent (§9): the files are already safely on the target, this is best-effort
                // subtree cleanup — log the full exception and continue with the remaining dirs.
                _logger.LogWarning(ex, "Job {Id}: could not recycle source directory '{Dir}'.", job.Id, dirPath);
            }
        }

        if (keptNonEmpty.Count > 0)
        {
            _logger.LogWarning(
                "Job {Id}: {Count} source directory(ies) kept because they still contain uncopied content: {Dirs}.",
                job.Id, keptNonEmpty.Count, string.Join("; ", keptNonEmpty));
            await _notifications.PublishAsync(
                NotificationSeverity.Warning,
                "Coda",
                "Contenuto non copiato rimasto sul sorgente",
                $"Lo spostamento della cartella '{srcRoot}' ha lasciato sul volume di origine " +
                $"{keptNonEmpty.Count} cartella/e con contenuto non indicizzato (mai copiato): " +
                $"{string.Join("; ", keptNonEmpty)}. Nessun file è stato eliminato senza copia verificata.",
                job.SourceVolumeId,
                ct);
        }

        return removed;
    }

    /// <summary>
    /// Reconstructs the moved folder's original root path. Source and target of every item share
    /// the same tail below their respective roots (<c>srcRoot\tail</c> ↔ <c>TargetRelativePath\tail</c>),
    /// so stripping that tail off one item's source yields the root — independent of how deep the
    /// files sit.
    /// </summary>
    /// <summary>
    /// The folder marker is the MoveFolder item that stands for the folder itself:
    /// no FileId AND a target equal to the job's destination root. The second condition
    /// is what tells it apart from a legacy/manually-seeded FILE item that merely lacks
    /// a FileId — file items always target a path BELOW the destination root.
    /// </summary>
    private static OperationJobItem? FindFolderMarker(OperationJob job, bool pendingOnly)
    {
        if (job.Type != JobType.MoveFolder) return null;
        return job.Items.FirstOrDefault(i =>
            i.FileId is null &&
            string.Equals(i.TargetRelativePath, job.TargetRelativePath, StringComparison.OrdinalIgnoreCase) &&
            (!pendingOnly || i.State == JobItemState.Pending));
    }

    private static string ResolveSourceRoot(OperationJob job)
    {
        // The folder marker item carries the root verbatim. Jobs enqueued before the
        // marker existed fall back to tail-stripping below.
        var marker = FindFolderMarker(job, pendingOnly: false);
        if (marker is not null)
            return marker.SourceRelativePath;

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
        // Not a blind UPDATE: the State concurrency token makes this throw
        // DbUpdateConcurrencyException if the committed state changed underneath.
        job.State = newState;
        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("Job {Id}: → {State}.", job.Id, newState);
    }

    /// <summary>
    /// Recovery path for a tripped State concurrency token: drops every pending mutation of
    /// the aborted run, reloads the committed row and, when the committed state is
    /// <see cref="JobState.Cancelled"/>, runs the same cleanup the regular cancel path runs
    /// (partials removed, landed items reconciled). The engine never writes over the
    /// committed state — and never lets the aborted run's tracked edits (space fold, index
    /// re-points) leak into the follow-up saves.
    /// </summary>
    private async Task HandleConcurrentStateChangeAsync(OperationJob staleJob)
    {
        _db.ChangeTracker.Clear();
        var job = await _db.OperationJobs
            .Include(j => j.Items)
            .Include(j => j.SourceVolume)
            .Include(j => j.TargetVolume)
            .FirstOrDefaultAsync(j => j.Id == staleJob.Id, CancellationToken.None);
        if (job is null) return;

        if (job.State == JobState.Cancelled)
        {
            _logger.LogInformation(
                "Job {Id}: cancelled concurrently during a state transition — aborting; source left untouched.",
                job.Id);
            await CleanupPartialsAsync(job);
            // The API's CancelAsync clears the overlay in its own transaction; repeating it here
            // is idempotent and covers the cancel path that never reached that clear.
            await _overlay.ClearForJobAsync(job.Id, CancellationToken.None);
            await _indexUpdater.ReconcileCancelledJobAsync(job, CancellationToken.None);
            return;
        }

        // A genuine concurrent transition: keep the committed state; if it is still
        // runnable the worker re-picks the job and it resumes from its checkpoint.
        _logger.LogWarning(
            "Job {Id}: committed state is {State}, diverging from this run's attempted write — " +
            "execution aborted, committed state kept.",
            job.Id, job.State);
    }

    private Task CompleteJobAsync(OperationJob job, CancellationToken ct) =>
        CompleteJobAsync(job, removedSourceDirPaths: [], ct);

    private async Task CompleteJobAsync(
        OperationJob job, IReadOnlyCollection<string> removedSourceDirPaths, CancellationToken ct)
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

        // One atomic completion commit:
        // - catalog/FTS update INSIDE it (finding #7): a failure rolls the whole completion
        //   back — the job stays at its checkpoint and re-runs — instead of flipping an
        //   already-committed Completed to Failed; and since the space fold above commits
        //   with it, a retry can never subtract RequiredBytesTarget twice.
        // - ledger release INSIDE it (finding #5): a crash can't leave a phantom IsActive
        //   reservation on a terminal job. The in-memory mirror follows after the commit.
        // - overlay clear INSIDE it (§5): the projection stops being an overlay the instant the
        //   physical fact it stood for is applied — never a window where both are on screen.
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _indexUpdater.UpdateAfterCompletionAsync(job, removedSourceDirPaths, ct);
            await _db.SaveChangesAsync(ct);
            await _overlay.ClearForJobAsync(job.Id, ct);
            await SpaceLedger.DeactivateEntriesAsync(_db, job.Id, ct);
            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException) { throw; }          // shutdown: resume next start
        catch (DbUpdateConcurrencyException) { throw; }        // cancel raced: outer handler follows it
        catch (Exception ex)
        {
            await HandleCompletionCommitFailureAsync(job, ex, ct);
            return;
        }
        await _ledger.ReleaseInMemoryAsync(job.Id, CancellationToken.None);
        _logger.LogInformation("Job {Id} completed.", job.Id);
    }

    /// <summary>
    /// Bounded retry budget for completion-commit failures. Transient causes (SQLITE_BUSY)
    /// resolve within a re-pick or two; hitting the budget means the failure is persistent
    /// and the job must be parked instead of livelocking the FIFO queue.
    /// </summary>
    private const int MaxCompletionAttempts = 3;

    /// <summary>
    /// The completion transaction rolled back (typically the index/FTS update failed). The
    /// job's physical work is done and its checkpoint is intact, so for a transient cause
    /// the right move is to leave it runnable and let the worker re-pick it. But the
    /// re-pick is immediate: a PERSISTENT failure would starve the whole FIFO queue — so
    /// the attempts are counted on <see cref="OperationJob.RetryCount"/> and past the
    /// budget the job is parked <see cref="JobState.Failed"/> (visible in the UI bell,
    /// manually retryable) WITHOUT the failing index update.
    /// </summary>
    private async Task HandleCompletionCommitFailureAsync(OperationJob job, Exception failure, CancellationToken ct)
    {
        // The tracker still holds every pending mutation of the rolled-back completion
        // (Completed state, space fold, index re-points). Drop them all — only the
        // committed checkpoint may reach the DB from here on.
        _db.ChangeTracker.Clear();

        var attempts = await _db.OperationJobs.AsNoTracking()
            .Where(j => j.Id == job.Id).Select(j => j.RetryCount)
            .FirstAsync(CancellationToken.None) + 1;
        // RetryCount-only conditional-free update: it must persist even if a cancel races.
        await _db.OperationJobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.RetryCount, attempts), CancellationToken.None);

        if (attempts < MaxCompletionAttempts)
        {
            _logger.LogWarning(failure,
                "Job {Id}: completion commit failed (attempt {N}/{Max}) — rolled back; " +
                "the job stays at its checkpoint and re-runs.",
                job.Id, attempts, MaxCompletionAttempts);
            return;
        }

        _logger.LogError(failure,
            "Job {Id}: completion commit failed {Max} times — parking the job as Failed.",
            job.Id, MaxCompletionAttempts);
        var reloaded = await _db.OperationJobs
            .Include(j => j.Items)
            .Include(j => j.SourceVolume)
            .Include(j => j.TargetVolume)
            .FirstAsync(j => j.Id == job.Id, CancellationToken.None);
        await SetFailedAsync(reloaded,
            $"Completamento fallito {MaxCompletionAttempts} volte durante l'aggiornamento " +
            $"dell'indice: {failure.Message}", ct);
    }

    private async Task SetBlockedAsync(OperationJob job, JobBlockReason reason, string message, CancellationToken ct)
    {
        job.State = JobState.Blocked;
        job.BlockReason = reason;
        job.ErrorMessage = message;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A Cancel raced the block: the committed state wins over ours.
            await HandleConcurrentStateChangeAsync(job);
            return;
        }
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
        try
        {
            // Same-transaction release as CompleteJobAsync (finding #5), and the same
            // same-transaction overlay clear (§5): Failed is terminal, so the projection must
            // stop promising an operation that will not happen unless the user retries — and
            // RetryAsync writes the overlay back when they do.
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.SaveChangesAsync(ct);
            await _overlay.ClearForJobAsync(job.Id, ct);
            await SpaceLedger.DeactivateEntriesAsync(_db, job.Id, ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A Cancel raced the failure: the committed state wins over ours.
            await HandleConcurrentStateChangeAsync(job);
            return;
        }
        await _ledger.ReleaseInMemoryAsync(job.Id, CancellationToken.None);

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
        // FIX #14: items already landed on the target keep their finalized copy indexed.
        await _indexUpdater.ReconcileCancelledJobAsync(job, CancellationToken.None);
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
