using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Paging;
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
    private static readonly HashSet<JobState> TerminalStates =
        [JobState.Completed, JobState.Failed, JobState.Cancelled];

    private readonly FileTracertDbContext _db;
    private readonly ISpaceLedger _ledger;
    private readonly IJobCancellationRegistry _cancellation;
    private readonly ILogger<QueueService> _logger;

    public QueueService(
        FileTracertDbContext db,
        ISpaceLedger ledger,
        IJobCancellationRegistry cancellation,
        ILogger<QueueService> logger)
    {
        _db = db;
        _ledger = ledger;
        _cancellation = cancellation;
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
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Reserve AFTER commit. If reservation fails, the job stays Pending but has no ledger entry;
        // the processor re-checks feasibility before executing so no data is at risk.
        if (shouldReserve)
        {
            await _ledger.ReserveAsync(
                job.Id,
                job.SequenceOrder,
                job.TargetVolumeId!.Value,
                job.RequiredBytesTarget,
                job.SourceVolumeId,
                job.FreedBytesSource,
                ct);
        }

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

        // Prospective job: it would land at the end of the queue, so all active deltas apply.
        return await _ledger.ComputeFeasibilityAsync(
            vol.Id, vol.FreeBytesLastKnown, vol.IsOnline, totalBytes,
            excludeJobId: null, sequenceOrder: null, ct);
    }

    public async Task CancelAsync(int jobId, CancellationToken ct)
    {
        var job = await _db.OperationJobs.FindAsync([jobId], ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        if (TerminalStates.Contains(job.State))
            throw new InvalidOperationException($"Job {jobId} is already terminal ({job.State}).");

        job.State = JobState.Cancelled;
        job.CompletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Signal the running job (if any) AFTER Cancelled is committed, so the engine both
        // sees the state on re-check and has its copy interrupted via the token.
        _cancellation.Cancel(jobId);

        await _ledger.ReleaseAsync(jobId, ct);
        _logger.LogInformation("Cancelled job {Id}.", jobId);
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
                feasibility = await _ledger.ComputeFeasibilityAsync(
                    job.TargetVolumeId.Value,
                    job.TargetVolume.FreeBytesLastKnown,
                    job.TargetVolume.IsOnline,
                    job.RequiredBytesTarget,
                    excludeJobId: job.Id,
                    sequenceOrder: job.SequenceOrder,
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

        if (!await _db.Volumes.AnyAsync(v => v.Id == req.TargetVolumeId.Value, ct))
            throw new InvalidOperationException($"Volume {req.TargetVolumeId} not found.");

        job.TargetVolumeId = req.TargetVolumeId;
        job.TargetRelativePath = req.TargetRelativePath;
        job.IsIntraVolume = true;
    }

    private async Task BuildRenameFileAsync(CreateJobRequest req, OperationJob job,
        List<OperationJobItem> items, CancellationToken ct)
    {
        if (req.SourceFileId is null || req.NewName is null)
            throw new ArgumentException("RenameFile requires SourceFileId and NewName.");

        await GuardFileAsync(req.SourceFileId.Value, ct);

        var file = await _db.Files
            .Include(f => f.Directory)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == req.SourceFileId.Value, ct)
            ?? throw new InvalidOperationException($"File {req.SourceFileId} not found.");

        var srcPath = JoinPath(file.Directory.MaterializedPath, file.Name);
        var dstPath = JoinPath(file.Directory.MaterializedPath, req.NewName);

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

        var dir = await _db.Directories.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == req.SourceDirectoryId.Value, ct)
            ?? throw new InvalidOperationException($"Directory {req.SourceDirectoryId} not found.");

        await GuardDirectoryAsync(req.SourceDirectoryId.Value, dir.MaterializedPath, dir.VolumeId, ct);

        var parentPath = ParentPath(dir.MaterializedPath);
        var dstPath = JoinPath(parentPath, req.NewName);

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
        var srcPath = JoinPath(file.Directory.MaterializedPath, file.Name);
        var dstPath = JoinPath(req.TargetRelativePath, file.Name);

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
                excludeJobId: null, sequenceOrder: null, ct);

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

        var dir = await _db.Directories.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == req.SourceDirectoryId.Value, ct)
            ?? throw new InvalidOperationException($"Directory {req.SourceDirectoryId} not found.");

        await GuardDirectoryAsync(req.SourceDirectoryId.Value, dir.MaterializedPath, dir.VolumeId, ct);

        var targetVol = await _db.Volumes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == req.TargetVolumeId.Value, ct)
            ?? throw new InvalidOperationException($"Volume {req.TargetVolumeId} not found.");

        bool intra = dir.VolumeId == targetVol.Id;
        var dstDirPath = JoinPath(req.TargetRelativePath, dir.Name);

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
                excludeJobId: null, sequenceOrder: null, ct);

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

            var srcFilePath = JoinPath(dirMatPath, f.Name);
            var dstFilePath = relWithinSrc.Length > 0
                ? dstDirPath + "\\" + relWithinSrc + "\\" + f.Name
                : JoinPath(dstDirPath, f.Name);

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
        bool busy = await _db.OperationJobItems
            .AnyAsync(i => i.SourceRelativePath == materializedPath &&
                           i.Job.SourceVolumeId == volumeId &&
                           !TerminalStates.Contains(i.Job.State), ct);
        if (busy)
            throw new EntityAlreadyPendingException("Directory", directoryId);
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

    // ── path helpers ──────────────────────────────────────────────────────────

    private static string JoinPath(string dir, string name) =>
        dir.Length == 0 ? name : dir + "\\" + name;

    private static string ParentPath(string path)
    {
        var idx = path.LastIndexOf('\\');
        return idx < 0 ? string.Empty : path[..idx];
    }
}
