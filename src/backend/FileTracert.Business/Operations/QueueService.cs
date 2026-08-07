using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Manages the job queue: enqueue, preview, cancel, list.
/// Scoped — one instance per request/scope, backed by a single DbContext.
/// </summary>
public sealed class QueueService : IQueueService
{
    private static readonly HashSet<JobState> TerminalStates = [.. JobStates.Terminal];

    private readonly FileTracertDbContext _db;
    private readonly ISpaceLedger _ledger;
    private readonly IJobCancellationRegistry _cancellation;
    private readonly IFileMover _mover;
    private readonly IQueueSignal _signal;
    private readonly IndexUpdater _indexUpdater;
    private readonly ILogger<QueueService> _logger;

    public QueueService(
        FileTracertDbContext db,
        ISpaceLedger ledger,
        IJobCancellationRegistry cancellation,
        IFileMover mover,
        IQueueSignal signal,
        IndexUpdater indexUpdater,
        ILogger<QueueService> logger)
    {
        _db = db;
        _ledger = ledger;
        _cancellation = cancellation;
        _mover = mover;
        _signal = signal;
        _indexUpdater = indexUpdater;
        _logger = logger;
    }

    // ── IQueueService ─────────────────────────────────────────────────────────

    public async Task<OperationJobDto> EnqueueAsync(CreateJobRequest request, CancellationToken ct)
    {
        var (job, items, shouldReserve) = await BuildJobAsync(request, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.OperationJobs.Add(job);
        foreach (var item in items)
            _db.OperationJobItems.Add(item);
        await _db.SaveChangesAsync(ct);   // assigns job.Id, still inside the open transaction

        // C3: stage the ledger reservation in the SAME transaction as the job, so the two commit
        // atomically. The old code reserved AFTER the commit — a throw or aborted request between
        // the two left the job Pending with no ledger entry, making every other job's feasibility
        // under-count this demand and overcommit the target.
        if (shouldReserve)
        {
            _db.SpaceLedgerEntries.AddRange(SpaceLedger.BuildReservationEntries(
                job.Id, job.TargetVolumeId!.Value, job.RequiredBytesTarget,
                job.SourceVolumeId, job.FreedBytesSource));
            await _db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);

        // Update the in-memory mirror ONLY after the DB commit succeeds: the durable entries and
        // the job now exist together, and the mirror can never claim a reservation the DB lacks.
        if (shouldReserve)
        {
            await _ledger.RegisterReservationInMemoryAsync(
                job.Id, job.SequenceOrder, job.TargetVolumeId!.Value,
                job.RequiredBytesTarget, job.SourceVolumeId, job.FreedBytesSource, ct);
        }

        // Wake the processor now instead of letting it discover the job on its next safety poll.
        _signal.Signal();

        _logger.LogInformation("Enqueued job {Id} type={Type} state={State}.", job.Id, job.Type, job.State);
        return MapToDto(job, items, null);
    }

    public async Task<FeasibilityResult> PreviewAsync(CreateJobRequest request, CancellationToken ct)
    {
        var (targetVolumeId, totalBytes) = await ResolvePreviewMetaAsync(request, ct);

        if (targetVolumeId is null || totalBytes == 0)
            return new FeasibilityResult(0, 0, long.MaxValue, 0, true, null, true);

        var vol = await _db.Volumes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == targetVolumeId.Value, ct)
            ?? throw new InvalidOperationException($"Target volume {targetVolumeId} not found.");

        // Prospective job: it would land at the end of the queue, so all active deltas apply
        // (planning view — promised liberations count, the queue materializes them in order).
        return await _ledger.ComputeFeasibilityAsync(
            vol.Id, vol.FreeBytesLastKnown, vol.IsOnline, totalBytes,
            excludeJobId: null, sequenceOrder: null, includeQueuedLiberations: true, ct);
    }

    public async Task<FeasibilityResult> PreviewBatchAsync(
        IReadOnlyList<CreateJobRequest> requests, CancellationToken ct)
    {
        // Aggregate the demand per target volume — the batch weighs on the ledger as a
        // whole, so it must be evaluated as a whole (previewing one file would lie).
        var demandByVolume = new Dictionary<int, long>();
        foreach (var request in requests)
        {
            var (targetVolumeId, totalBytes) = await ResolvePreviewMetaAsync(request, ct);
            if (targetVolumeId is null || totalBytes == 0)
                continue;
            demandByVolume[targetVolumeId.Value] =
                demandByVolume.GetValueOrDefault(targetVolumeId.Value) + totalBytes;
        }

        if (demandByVolume.Count == 0)
            return new FeasibilityResult(0, 0, long.MaxValue, 0, true, null, true);

        // Evaluate each involved volume; report the tightest one (smallest available−required
        // margin — for infeasible volumes that is the largest deficit).
        FeasibilityResult? tightest = null;
        foreach (var (volumeId, requiredBytes) in demandByVolume)
        {
            var vol = await _db.Volumes.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == volumeId, ct)
                ?? throw new InvalidOperationException($"Target volume {volumeId} not found.");

            var f = await _ledger.ComputeFeasibilityAsync(
                vol.Id, vol.FreeBytesLastKnown, vol.IsOnline, requiredBytes,
                excludeJobId: null, sequenceOrder: null, includeQueuedLiberations: true, ct);

            if (tightest is null ||
                f.AvailableEstimateBytes - f.RequiredBytes < tightest.AvailableEstimateBytes - tightest.RequiredBytes)
            {
                tightest = f;
            }
        }

        return tightest!;
    }

    public async Task CancelAsync(int jobId, CancellationToken ct)
    {
        var job = await _db.OperationJobs
            .Include(j => j.Items)
            .Include(j => j.TargetVolume)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        // The State concurrency token (finding #2) can trip if the engine commits a transition
        // between our read and our write. A cancel must win over any non-terminal state, so
        // reload and reapply; the loop only ends when Cancelled is committed or the job turned
        // terminal on its own.
        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            if (TerminalStates.Contains(job.State))
                throw new InvalidOperationException($"Job {jobId} is already terminal ({job.State}).");

            job.State = JobState.Cancelled;
            job.CompletedUtc = DateTime.UtcNow;
            try
            {
                // Cancelled + ledger release commit atomically (finding #5): a crash in
                // between must not leave a phantom IsActive reservation.
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await _db.SaveChangesAsync(ct);
                await SpaceLedger.DeactivateEntriesAsync(_db, jobId, ct);
                await tx.CommitAsync(ct);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                _logger.LogInformation(
                    "Cancel of job {Id}: state moved concurrently (attempt {N}) — reloading and reapplying.",
                    jobId, attempt);
                await _db.Entry(job).ReloadAsync(ct);
            }
        }

        // Signal the running job (if any) AFTER Cancelled is committed, so the engine both
        // sees the state on re-check and has its copy interrupted via the token.
        _cancellation.Cancel(jobId);

        // DB rows were deactivated inside the commit above — mirror it in memory.
        await _ledger.ReleaseInMemoryAsync(jobId, ct);

        // FIX #10-partial: a job cancelled while NOT running (e.g. checkpointed in Copying
        // after a shutdown, or Blocked with copied items) has orphan .fadit-partial files no
        // engine pass will ever sweep. A running job's engine does its own cleanup; here a
        // locked partial just logs and stays for the engine to remove.
        CleanupPartials(job);
        await _db.SaveChangesAsync(ct);

        // FIX #14: items already finalized on the target (or whose source is recycled)
        // must be re-pointed in the index, or the Catalog shows ghosts and the target
        // holds untracked copies. The engine repeats this for a running job — idempotent.
        await _indexUpdater.ReconcileCancelledJobAsync(job, ct);

        _logger.LogInformation("Cancelled job {Id}.", jobId);
    }

    public async Task<OperationJobDto> RetryAsync(int jobId, CancellationToken ct)
    {
        var job = await _db.OperationJobs
            .Include(j => j.Items)
            .Include(j => j.SourceVolume)
            .Include(j => j.TargetVolume)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        if (job.State is not (JobState.Blocked or JobState.Failed))
            throw new InvalidOperationException(
                $"Job {jobId} is not retryable in state {job.State} (only Blocked or Failed).");

        // Leftover partials are garbage: the retry re-copies from scratch (fix #10).
        CleanupPartials(job);

        // Reset every item whose copy did not reach finalization. Verified items keep their
        // finalized target file; Done items already lost their source — both must NOT re-copy.
        foreach (var item in job.Items.Where(i =>
                     i.State is JobItemState.Pending or JobItemState.Copying
                               or JobItemState.Copied or JobItemState.Failed))
        {
            item.State = JobItemState.Pending;
            item.BytesCopied = 0;
            item.ErrorMessage = null;
        }

        job.State = JobState.Pending;
        job.BlockReason = JobBlockReason.None;
        job.ErrorMessage = null;
        job.CompletedUtc = null;
        job.RetryCount++;
        await _db.SaveChangesAsync(ct);

        // Ledger coherence (same principle as the atomic enqueue, fix #2): a Failed job
        // released its reservation, an engine-Blocked one kept it, an enqueue-Blocked one
        // never had it — release-then-reserve normalizes all three to exactly one set.
        if (!job.IsIntraVolume && job.RequiredBytesTarget > 0 && job.TargetVolumeId.HasValue)
        {
            await _ledger.ReleaseAsync(job.Id, ct);
            await _ledger.ReserveAsync(
                job.Id, job.SequenceOrder, job.TargetVolumeId.Value,
                job.RequiredBytesTarget, job.SourceVolumeId, job.FreedBytesSource, ct);
        }

        // A retried job is runnable again — wake the processor.
        _signal.Signal();

        _logger.LogInformation("Job {Id} manually retried (attempt {N}).", job.Id, job.RetryCount);
        return MapToDto(job, [.. job.Items], null);
    }

    private void CleanupPartials(OperationJob job)
    {
        var tgtGuid = job.TargetVolume?.VolumeGuid;
        if (tgtGuid is null) return;

        foreach (var item in job.Items.Where(i => !string.IsNullOrEmpty(i.TempPath)))
        {
            try
            {
                if (_mover.Exists(tgtGuid, item.TempPath!))
                    _mover.DeleteToRecycleBin(tgtGuid, item.TempPath!);
                item.TempPath = null;
            }
            catch (Exception ex)
            {
                // Not silent: logged in full; the partial is garbage on a terminal job, the
                // user's action (cancel) still succeeded — no need to fail the request.
                _logger.LogWarning(ex, "Job {Id}: could not remove orphan partial '{Path}'.",
                    job.Id, item.TempPath);
            }
        }
    }

    public async Task<PagedResult<OperationJobDto>> ListAsync(int skip, int take, CancellationToken ct)
    {
        var total = await _db.OperationJobs.CountAsync(ct);

        var jobs = await _db.OperationJobs
            .Include(j => j.SourceVolume)
            .Include(j => j.TargetVolume)
            .Include(j => j.Items)
            .OrderBy(j => j.SequenceOrder)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(ct);

        var dtos = new List<OperationJobDto>(jobs.Count);
        foreach (var job in jobs)
        {
            FeasibilityResult? feasibility = null;
            if (job.State == JobState.Blocked && job.TargetVolumeId.HasValue && job.TargetVolume is not null)
            {
                // Hard view: the deficit shown for a Blocked job must explain the block,
                // i.e. match the engine's execution-time re-check, not the planning estimate.
                feasibility = await _ledger.ComputeFeasibilityAsync(
                    job.TargetVolumeId.Value,
                    job.TargetVolume.FreeBytesLastKnown,
                    job.TargetVolume.IsOnline,
                    job.RequiredBytesTarget,
                    excludeJobId: job.Id,
                    sequenceOrder: job.SequenceOrder,
                    includeQueuedLiberations: false,
                    ct);
            }
            dtos.Add(MapToDto(job, [.. job.Items], feasibility));
        }

        return new PagedResult<OperationJobDto>(dtos, total, skip, take);
    }

    // ── private: job building ─────────────────────────────────────────────────

    private async Task<(OperationJob job, List<OperationJobItem> items, bool shouldReserve)>
        BuildJobAsync(CreateJobRequest request, CancellationToken ct)
    {
        var maxOrder = await _db.OperationJobs.MaxAsync(j => (int?)j.SequenceOrder, ct) ?? 0;
        var job = new OperationJob
        {
            Type = request.Type,
            State = JobState.Pending,
            BlockReason = JobBlockReason.None,
            SequenceOrder = maxOrder + 1,
            EstimateIsLive = true
        };

        List<OperationJobItem> items = [];
        bool shouldReserve = false;

        switch (request.Type)
        {
            case JobType.CreateFolder:
                await BuildCreateFolderAsync(request, job, ct);
                break;
            case JobType.RenameFile:
                await BuildRenameFileAsync(request, job, items, ct);
                break;
            case JobType.RenameFolder:
                await BuildRenameFolderAsync(request, job, items, ct);
                break;
            case JobType.MoveFile:
                shouldReserve = await BuildMoveFileAsync(request, job, items, ct);
                break;
            case JobType.MoveFolder:
                shouldReserve = await BuildMoveFolderAsync(request, job, items, ct);
                break;
            default:
                throw new InvalidOperationException($"Unsupported job type: {request.Type}");
        }

        foreach (var item in items)
            item.Job = job;

        return (job, items, shouldReserve);
    }

    private async Task BuildCreateFolderAsync(CreateJobRequest req, OperationJob job, CancellationToken ct)
    {
        if (req.TargetVolumeId is null || req.TargetRelativePath is null)
            throw new ArgumentException("CreateFolder requires TargetVolumeId and TargetRelativePath.");

        if (!OperationName.TryValidatePath(req.TargetRelativePath, allowRoot: false, out var folderPath, out var pathError))
            throw new ArgumentException(pathError);

        if (!await _db.Volumes.AnyAsync(v => v.Id == req.TargetVolumeId.Value, ct))
            throw new InvalidOperationException($"Volume {req.TargetVolumeId} not found.");

        job.TargetVolumeId = req.TargetVolumeId;
        job.TargetRelativePath = folderPath;
        job.IsIntraVolume = true;
    }

    private async Task BuildRenameFileAsync(CreateJobRequest req, OperationJob job,
        List<OperationJobItem> items, CancellationToken ct)
    {
        if (req.SourceFileId is null || req.NewName is null)
            throw new ArgumentException("RenameFile requires SourceFileId and NewName.");

        if (!OperationName.TryValidateLeaf(req.NewName, out var nameError))
            throw new ArgumentException(nameError);

        await GuardFileAsync(req.SourceFileId.Value, ct);

        var file = await _db.Files
            .Include(f => f.Directory)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == req.SourceFileId.Value, ct)
            ?? throw new InvalidOperationException($"File {req.SourceFileId} not found.");

        var srcPath = ScanPath.Join(file.Directory.MaterializedPath, file.Name);
        var dstPath = ScanPath.Join(file.Directory.MaterializedPath, req.NewName);

        job.SourceVolumeId = file.VolumeId;
        job.TargetVolumeId = file.VolumeId;
        job.TargetRelativePath = req.NewName;
        job.IsIntraVolume = true;

        items.Add(new OperationJobItem
        {
            FileId = file.Id,
            SourceRelativePath = srcPath,
            TargetRelativePath = dstPath,
            SizeBytes = file.SizeBytes,
            State = JobItemState.Pending
        });
    }

    private async Task BuildRenameFolderAsync(CreateJobRequest req, OperationJob job,
        List<OperationJobItem> items, CancellationToken ct)
    {
        if (req.SourceDirectoryId is null || req.NewName is null)
            throw new ArgumentException("RenameFolder requires SourceDirectoryId and NewName.");

        if (!OperationName.TryValidateLeaf(req.NewName, out var nameError))
            throw new ArgumentException(nameError);

        var dir = await _db.Directories.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == req.SourceDirectoryId.Value, ct)
            ?? throw new InvalidOperationException($"Directory {req.SourceDirectoryId} not found.");

        await GuardDirectoryAsync(req.SourceDirectoryId.Value, dir.MaterializedPath, dir.VolumeId, ct);

        var parentPath = ScanPath.Parent(dir.MaterializedPath);
        var dstPath = ScanPath.Join(parentPath, req.NewName);

        job.SourceVolumeId = dir.VolumeId;
        job.TargetVolumeId = dir.VolumeId;
        job.TargetRelativePath = req.NewName;
        job.IsIntraVolume = true;

        items.Add(new OperationJobItem
        {
            FileId = null,
            SourceRelativePath = dir.MaterializedPath,
            TargetRelativePath = dstPath,
            State = JobItemState.Pending
        });
    }

    private async Task<bool> BuildMoveFileAsync(CreateJobRequest req, OperationJob job,
        List<OperationJobItem> items, CancellationToken ct)
    {
        if (req.SourceFileId is null || req.TargetVolumeId is null || req.TargetRelativePath is null)
            throw new ArgumentException("MoveFile requires SourceFileId, TargetVolumeId and TargetRelativePath.");

        if (!OperationName.TryValidatePath(req.TargetRelativePath, allowRoot: true, out var targetPath, out var pathError))
            throw new ArgumentException(pathError);

        await GuardFileAsync(req.SourceFileId.Value, ct);

        var file = await _db.Files
            .Include(f => f.Directory)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == req.SourceFileId.Value, ct)
            ?? throw new InvalidOperationException($"File {req.SourceFileId} not found.");

        var targetVol = await _db.Volumes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == req.TargetVolumeId.Value, ct)
            ?? throw new InvalidOperationException($"Volume {req.TargetVolumeId} not found.");

        bool intra = file.VolumeId == targetVol.Id;
        var srcPath = ScanPath.Join(file.Directory.MaterializedPath, file.Name);
        var dstPath = ScanPath.Join(targetPath, file.Name);

        job.SourceVolumeId = file.VolumeId;
        job.TargetVolumeId = targetVol.Id;
        job.TargetRelativePath = dstPath;
        job.IsIntraVolume = intra;

        if (!intra)
        {
            job.TotalBytes = file.SizeBytes;
            job.RequiredBytesTarget = file.SizeBytes;
            job.FreedBytesSource = file.SizeBytes;

            var f = await _ledger.ComputeFeasibilityAsync(
                targetVol.Id, targetVol.FreeBytesLastKnown, targetVol.IsOnline, file.SizeBytes,
                excludeJobId: null, sequenceOrder: null, includeQueuedLiberations: true, ct);

            job.EstimateIsLive = f.EstimateIsLive;
            if (!f.Feasible)
            {
                job.State = JobState.Blocked;
                job.BlockReason = JobBlockReason.InsufficientSpace;
            }
        }

        items.Add(new OperationJobItem
        {
            FileId = file.Id,
            SourceRelativePath = srcPath,
            TargetRelativePath = dstPath,
            SizeBytes = file.SizeBytes,
            State = JobItemState.Pending
        });

        return !intra && job.State == JobState.Pending;
    }

    private async Task<bool> BuildMoveFolderAsync(CreateJobRequest req, OperationJob job,
        List<OperationJobItem> items, CancellationToken ct)
    {
        if (req.SourceDirectoryId is null || req.TargetVolumeId is null || req.TargetRelativePath is null)
            throw new ArgumentException("MoveFolder requires SourceDirectoryId, TargetVolumeId and TargetRelativePath.");

        if (!OperationName.TryValidatePath(req.TargetRelativePath, allowRoot: true, out var targetPath, out var pathError))
            throw new ArgumentException(pathError);

        var dir = await _db.Directories.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == req.SourceDirectoryId.Value, ct)
            ?? throw new InvalidOperationException($"Directory {req.SourceDirectoryId} not found.");

        await GuardDirectoryAsync(req.SourceDirectoryId.Value, dir.MaterializedPath, dir.VolumeId, ct);

        var targetVol = await _db.Volumes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == req.TargetVolumeId.Value, ct)
            ?? throw new InvalidOperationException($"Volume {req.TargetVolumeId} not found.");

        bool intra = dir.VolumeId == targetVol.Id;
        var dstDirPath = ScanPath.Join(targetPath, dir.Name);

        // C22: geometrically impossible or pointless moves are a 400 at enqueue, not a
        // Failed job at execution. Both checks are intra-volume only — on another volume
        // the same relative path is a different physical location.
        if (intra)
        {
            if (ScanPath.IsWithin(targetPath, dir.MaterializedPath))
                throw new ArgumentException(
                    $"Impossibile spostare la cartella '{dir.MaterializedPath}' dentro sé stessa " +
                    $"o in una sua sottocartella ('{targetPath}').");

            if (string.Equals(dstDirPath, dir.MaterializedPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"La cartella '{dir.MaterializedPath}' si trova già in questa posizione.");
        }

        job.SourceVolumeId = dir.VolumeId;
        job.TargetVolumeId = targetVol.Id;
        job.TargetRelativePath = dstDirPath;
        job.IsIntraVolume = intra;

        if (intra)
        {
            items.Add(new OperationJobItem
            {
                FileId = null,
                SourceRelativePath = dir.MaterializedPath,
                TargetRelativePath = dstDirPath,
                State = JobItemState.Pending
            });
            return false;
        }

        // Root marker item (FileId = null): represents the folder itself. It gives the
        // engine the source root path and guarantees real work happens — the target folder
        // is created even when the subtree holds no indexed file (C21: an empty or
        // all-excluded folder must never reach Completed without a syscall).
        items.Add(new OperationJobItem
        {
            FileId = null,
            SourceRelativePath = dir.MaterializedPath,
            TargetRelativePath = dstDirPath,
            SizeBytes = 0,
            State = JobItemState.Pending
        });

        // Cross-volume: expand to one item per file in the subtree.
        var expanded = await ExpandSubtreeAsync(dir, dstDirPath, ct);
        items.AddRange(expanded);

        long total = expanded.Sum(i => i.SizeBytes);
        job.TotalBytes = total;
        job.RequiredBytesTarget = total;
        job.FreedBytesSource = total;

        if (total > 0)
        {
            var f = await _ledger.ComputeFeasibilityAsync(
                targetVol.Id, targetVol.FreeBytesLastKnown, targetVol.IsOnline, total,
                excludeJobId: null, sequenceOrder: null, includeQueuedLiberations: true, ct);

            job.EstimateIsLive = f.EstimateIsLive;
            if (!f.Feasible)
            {
                job.State = JobState.Blocked;
                job.BlockReason = JobBlockReason.InsufficientSpace;
            }
        }

        return job.State == JobState.Pending && total > 0;
    }

    // ── private: subtree expansion ────────────────────────────────────────────

    private async Task<List<OperationJobItem>> ExpandSubtreeAsync(
        DirectoryNode sourceDir, string dstDirPath, CancellationToken ct)
    {
        var srcPath = sourceDir.MaterializedPath;
        var prefixWithSep = srcPath + "\\";

        var dirIds = await _db.Directories
            .Where(d => d.VolumeId == sourceDir.VolumeId &&
                        (d.Id == sourceDir.Id || d.MaterializedPath.StartsWith(prefixWithSep)))
            .Select(d => new { d.Id, d.MaterializedPath })
            .ToListAsync(ct);

        var dirIdSet = dirIds.ToDictionary(d => d.Id, d => d.MaterializedPath);

        var files = await _db.Files
            .AsNoTracking()
            .Where(f => f.IsPresent && f.IsIncluded && dirIdSet.Keys.Contains(f.DirectoryId))
            .Select(f => new { f.Id, f.DirectoryId, f.Name, f.SizeBytes })
            .ToListAsync(ct);

        return files.Select(f =>
        {
            var dirMatPath = dirIdSet[f.DirectoryId];
            // strip source dir prefix to get the relative-within-subtree path
            var relWithinSrc = dirMatPath.Length > srcPath.Length
                ? dirMatPath[(srcPath.Length + 1)..]
                : string.Empty;

            var srcFilePath = ScanPath.Join(dirMatPath, f.Name);
            var dstFilePath = relWithinSrc.Length > 0
                ? dstDirPath + "\\" + relWithinSrc + "\\" + f.Name
                : ScanPath.Join(dstDirPath, f.Name);

            return new OperationJobItem
            {
                FileId = f.Id,
                SourceRelativePath = srcFilePath,
                TargetRelativePath = dstFilePath,
                SizeBytes = f.SizeBytes,
                State = JobItemState.Pending
            };
        }).ToList();
    }

    // ── private: guards ────────────────────────────────────────────────────────

    private async Task GuardFileAsync(int fileId, CancellationToken ct)
    {
        bool busy = await _db.OperationJobItems
            .AnyAsync(i => i.FileId == fileId &&
                           !TerminalStates.Contains(i.Job.State), ct);
        if (busy)
            throw new EntityAlreadyPendingException("File", fileId);
    }

    private async Task GuardDirectoryAsync(int directoryId, string materializedPath,
        int volumeId, CancellationToken ct)
    {
        // Overlap, not just exact match: a pending op on an ancestor OR a descendant of this
        // directory makes the two operations touch the same subtree, which must be serialized
        // (MVP: one op per subtree). Two cases, both segment-boundary aware so "Docs" never
        // matches "Documents":
        //   • an existing op sits on this dir or one of its ANCESTORS  → its path ∈ our ancestor set
        //   • an existing op sits on one of our DESCENDANTS            → its path starts with "us\"
        var ancestors = AncestorPaths(materializedPath);
        var descendantPrefix = materializedPath + "\\";

        bool busy = await _db.OperationJobItems
            .AnyAsync(i => i.Job.SourceVolumeId == volumeId &&
                           !TerminalStates.Contains(i.Job.State) &&
                           (ancestors.Contains(i.SourceRelativePath) ||
                            i.SourceRelativePath.StartsWith(descendantPrefix)), ct);
        if (busy)
            throw new EntityAlreadyPendingException("Directory", directoryId);
    }

    /// <summary>
    /// Every path from <paramref name="path"/> up to (and including) the volume root, e.g.
    /// <c>A\B\C</c> → <c>[A\B\C, A\B, A]</c>. Used to detect an ancestor op in a single SQL <c>IN</c>.
    /// </summary>
    private static List<string> AncestorPaths(string path)
    {
        var result = new List<string>();
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            result.Add(current);
            var idx = current.LastIndexOf('\\');
            current = idx < 0 ? string.Empty : current[..idx];
        }
        return result;
    }

    // ── private: preview meta (no guards, no side effects) ────────────────────

    private async Task<(int? targetVolumeId, long totalBytes)> ResolvePreviewMetaAsync(
        CreateJobRequest req, CancellationToken ct)
    {
        switch (req.Type)
        {
            case JobType.CreateFolder:
                return (req.TargetVolumeId, 0);

            case JobType.RenameFile:
            case JobType.RenameFolder:
                return (null, 0); // always intra-volume

            case JobType.MoveFile when req.SourceFileId.HasValue && req.TargetVolumeId.HasValue:
            {
                var file = await _db.Files.AsNoTracking()
                    .Select(f => new { f.Id, f.VolumeId, f.SizeBytes })
                    .FirstOrDefaultAsync(f => f.Id == req.SourceFileId.Value, ct);
                if (file is null) return (req.TargetVolumeId, 0);
                bool intra = file.VolumeId == req.TargetVolumeId.Value;
                return (req.TargetVolumeId, intra ? 0 : file.SizeBytes);
            }

            case JobType.MoveFolder when req.SourceDirectoryId.HasValue && req.TargetVolumeId.HasValue:
            {
                var dir = await _db.Directories.AsNoTracking()
                    .Select(d => new { d.Id, d.VolumeId, d.MaterializedPath })
                    .FirstOrDefaultAsync(d => d.Id == req.SourceDirectoryId.Value, ct);
                if (dir is null) return (req.TargetVolumeId, 0);
                bool intra = dir.VolumeId == req.TargetVolumeId.Value;
                if (intra) return (req.TargetVolumeId, 0);

                var prefixWithSep = dir.MaterializedPath + "\\";
                var dirIds = await _db.Directories
                    .Where(d => d.VolumeId == dir.VolumeId &&
                                (d.Id == dir.Id || d.MaterializedPath.StartsWith(prefixWithSep)))
                    .Select(d => d.Id)
                    .ToListAsync(ct);

                var total = await _db.Files.AsNoTracking()
                    .Where(f => f.IsPresent && f.IsIncluded && dirIds.Contains(f.DirectoryId))
                    .SumAsync(f => f.SizeBytes, ct);

                return (req.TargetVolumeId, total);
            }

            default:
                return (null, 0);
        }
    }

    // ── private: mapping ───────────────────────────────────────────────────────

    private static OperationJobDto MapToDto(OperationJob job, List<OperationJobItem> items,
        FeasibilityResult? feasibility)
    {
        return new OperationJobDto
        {
            Id = job.Id,
            Type = job.Type.ToString(),
            State = job.State.ToString(),
            BlockReason = job.BlockReason.ToString(),
            SourceVolumeId = job.SourceVolumeId,
            SourceVolumeLabel = job.SourceVolume?.Label,
            TargetVolumeId = job.TargetVolumeId,
            TargetVolumeLabel = job.TargetVolume?.Label,
            SourcePath = items.FirstOrDefault()?.SourceRelativePath,
            TargetPath = job.TargetRelativePath,
            IsIntraVolume = job.IsIntraVolume,
            TotalBytes = job.TotalBytes,
            BytesProcessed = job.BytesProcessed,
            RequiredBytesTarget = job.RequiredBytesTarget,
            FreedBytesSource = job.FreedBytesSource,
            EstimateIsLive = job.EstimateIsLive,
            SequenceOrder = job.SequenceOrder,
            ErrorMessage = job.ErrorMessage,
            CreatedUtc = job.CreatedUtc,
            StartedUtc = job.StartedUtc,
            CompletedUtc = job.CompletedUtc,
            Feasibility = feasibility
        };
    }

}
