using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Search;
using FileTracert.Data.Entities;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Projection;

/// <summary>
/// The one place where the <c>Pending*</c> overlay of §5 is written and cleared.
///
/// Writing it is what makes the Catalog a projection instead of a photograph of the disk:
/// the moment an operation is queued the entity is shown under its new name, in its new
/// folder, on its new volume, with a badge. Clearing it is what stops the projection from
/// lying: an overlay that outlives its job shows a file in a folder it will never reach.
///
/// Both halves run inside the caller's transaction — the enqueue's, the completion's, the
/// cancel's — so job state and projection commit together or not at all.
///
/// Deliberately a SINGLE entry point (<see cref="ApplyAsync"/>): step 9c makes it conditional
/// (a job born <c>Blocked(DependencyPending)</c> does not own its entity yet and writes no
/// overlay until it is unblocked), and that must be one edit, not a hunt for scattered
/// <c>Pending* =</c> assignments.
/// </summary>
public sealed class OverlayWriter
{
    private static readonly HashSet<JobState> TerminalStates = [.. JobStates.Terminal];

    private readonly FileTracertDbContext _db;
    private readonly DirectoryResolver _directories;
    private readonly IFileSearchIndex _fts;
    private readonly ILogger<OverlayWriter> _logger;

    public OverlayWriter(
        FileTracertDbContext db,
        DirectoryResolver directories,
        IFileSearchIndex fts,
        ILogger<OverlayWriter> logger)
    {
        _db = db;
        _directories = directories;
        _fts = fts;
        _logger = logger;
    }

    // ── write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stamps the overlay for a job that has just entered the queue (or re-entered it via
    /// <c>RetryAsync</c>). Everything it needs is read back from the persisted job and its
    /// items, so enqueue and retry share one code path and a retry can never rebuild a
    /// different projection from the original enqueue.
    ///
    /// Idempotent: re-running it on a job that already owns its overlay rewrites the same
    /// values and resolves the same directory rows.
    /// </summary>
    public async Task ApplyAsync(
        OperationJob job, IReadOnlyCollection<OperationJobItem> items, CancellationToken ct)
    {
        switch (job.Type)
        {
            case JobType.CreateFolder:
                await ApplyCreateFolderAsync(job, ct);
                break;
            case JobType.RenameFile:
                await ApplyRenameFileAsync(job, items, ct);
                break;
            case JobType.RenameFolder:
                await ApplyRenameFolderAsync(job, items, ct);
                break;
            case JobType.MoveFile:
                await ApplyMoveFileAsync(job, items, ct);
                break;
            case JobType.MoveFolder:
                await ApplyMoveFolderAsync(job, items, ct);
                break;
        }
    }

    private async Task ApplyCreateFolderAsync(OperationJob job, CancellationToken ct)
    {
        if (job.TargetVolumeId is null || string.IsNullOrEmpty(job.TargetRelativePath))
        {
            _logger.LogWarning(
                "Job {Id}: CreateFolder without a target — no projection row created.", job.Id);
            return;
        }

        await _directories.FindOrCreateProjectedAsync(
            job.TargetVolumeId.Value, job.TargetRelativePath, job.Id, ct);
    }

    private async Task ApplyRenameFileAsync(
        OperationJob job, IReadOnlyCollection<OperationJobItem> items, CancellationToken ct)
    {
        var file = await LoadSourceFileAsync(job, items, ct);
        if (file is null) return;

        file.PendingName = ScanPath.Name(job.TargetRelativePath!);
        file.PendingState = EntityPendingState.PendingRename;
        file.PendingJobId = job.Id;
        await _db.SaveChangesAsync(ct);

        // The FTS name column carries the PROJECTED name, so the new name is searchable at once.
        await SyncFtsAsync([file.Id], ct);
    }

    private async Task ApplyRenameFolderAsync(
        OperationJob job, IReadOnlyCollection<OperationJobItem> items, CancellationToken ct)
    {
        var dir = await LoadSourceDirectoryAsync(job, items, ct);
        if (dir is null) return;

        dir.PendingName = ScanPath.Name(job.TargetRelativePath!);
        dir.PendingState = EntityPendingState.PendingRename;
        dir.PendingJobId = job.Id;
        await _db.SaveChangesAsync(ct);
        // No FTS write: a folder rename changes no file NAME (see Projected.FtsPath).
    }

    private async Task ApplyMoveFileAsync(
        OperationJob job, IReadOnlyCollection<OperationJobItem> items, CancellationToken ct)
    {
        var file = await LoadSourceFileAsync(job, items, ct);
        if (file is null || job.TargetVolumeId is null) return;

        var targetDir = await _directories.FindOrCreateProjectedAsync(
            job.TargetVolumeId.Value, ScanPath.Parent(job.TargetRelativePath!), job.Id, ct);

        // The row stays on the SOURCE volume until the move actually happens; the projected
        // volume of the entity is the volume of its projected directory, which may well be
        // another one (§5, cross-volume). VolumeId only changes at execution.
        file.PendingDirectoryId = targetDir.Id;
        file.PendingState = EntityPendingState.PendingMove;
        file.PendingJobId = job.Id;
        await _db.SaveChangesAsync(ct);
        // No FTS write: the name does not change, and the path column stays physical.
    }

    private async Task ApplyMoveFolderAsync(
        OperationJob job, IReadOnlyCollection<OperationJobItem> items, CancellationToken ct)
    {
        var dir = await LoadSourceDirectoryAsync(job, items, ct);
        if (dir is null || job.TargetVolumeId is null) return;

        var targetParent = await _directories.FindOrCreateProjectedAsync(
            job.TargetVolumeId.Value, ScanPath.Parent(job.TargetRelativePath!), job.Id, ct);

        // ONE overlay for the whole move, on the folder row only: the descendants' projected
        // paths follow from walking the parents with the overlays applied (§5), so a
        // cross-volume move of 100 000 files still writes exactly one row here.
        dir.PendingParentId = targetParent.Id;
        dir.PendingState = EntityPendingState.PendingMove;
        dir.PendingJobId = job.Id;
        await _db.SaveChangesAsync(ct);
    }

    // ── clear ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drops every overlay owned by <paramref name="jobId"/>. Called on the terminal states
    /// only — <c>Completed</c> (after the physical fact is applied), <c>Cancelled</c>,
    /// <c>Failed</c> — always inside the transaction that commits that state.
    /// <c>Blocked</c> deliberately KEEPS its overlay: the job is still queued, so the
    /// projection must keep showing it.
    ///
    /// Idempotent: a second call finds nothing to clear.
    /// </summary>
    public async Task ClearForJobAsync(int jobId, CancellationToken ct)
    {
        var files = await _db.Files.Where(f => f.PendingJobId == jobId).ToListAsync(ct);
        var directories = await _db.Directories.Where(d => d.PendingJobId == jobId).ToListAsync(ct);

        if (files.Count == 0 && directories.Count == 0) return;

        foreach (var file in files) ClearFile(file);
        foreach (var dir in directories) ClearDirectory(dir);
        await _db.SaveChangesAsync(ct);

        // The FTS name column followed the overlay on the way in — it must follow it back out,
        // or a cancelled rename stays findable under a name that was never applied.
        await SyncFtsAsync(files.Select(f => f.Id).ToList(), ct);

        _logger.LogDebug(
            "Job {Id}: overlay cleared on {Files} file(s) and {Dirs} directory(ies).",
            jobId, files.Count, directories.Count);
    }

    /// <summary>
    /// Startup safety net: clears every overlay whose owning job no longer exists or is already
    /// terminal. Nothing should produce one — every write and every clear runs inside the
    /// transaction of the job's own state change — but a crash outside those transactions, a
    /// hand-edited database or an older build can, and an orphan overlay shows a file in a
    /// folder it will never reach. One query per table, run before the workers start.
    /// </summary>
    /// <returns>How many rows were cleaned up.</returns>
    public async Task<int> ReconcileOrphansAsync(CancellationToken ct)
    {
        var files = await _db.Files
            .Where(f => f.PendingState != EntityPendingState.None)
            .ToListAsync(ct);
        var directories = await _db.Directories
            .Where(d => d.PendingState != EntityPendingState.None)
            .ToListAsync(ct);

        if (files.Count == 0 && directories.Count == 0) return 0;

        var referenced = files.Select(f => f.PendingJobId)
            .Concat(directories.Select(d => d.PendingJobId))
            .Where(id => id.HasValue).Select(id => id!.Value)
            .Distinct().ToList();

        var liveJobs = await _db.OperationJobs
            .Where(j => referenced.Contains(j.Id) && !TerminalStates.Contains(j.State))
            .Select(j => j.Id)
            .ToListAsync(ct);
        var live = liveJobs.ToHashSet();

        var orphanFiles = files.Where(f => !IsOwned(f.PendingJobId, live)).ToList();
        var orphanDirs = directories.Where(d => !IsOwned(d.PendingJobId, live)).ToList();

        if (orphanFiles.Count == 0 && orphanDirs.Count == 0) return 0;

        foreach (var file in orphanFiles) ClearFile(file);
        foreach (var dir in orphanDirs) ClearDirectory(dir);
        await _db.SaveChangesAsync(ct);
        await SyncFtsAsync(orphanFiles.Select(f => f.Id).ToList(), ct);

        _logger.LogWarning(
            "Startup reconciliation: cleared {Files} orphan file overlay(s) and {Dirs} orphan " +
            "directory overlay(s) whose job no longer exists or is already terminal.",
            orphanFiles.Count, orphanDirs.Count);

        return orphanFiles.Count + orphanDirs.Count;
    }

    private static bool IsOwned(int? pendingJobId, HashSet<int> liveJobIds) =>
        pendingJobId.HasValue && liveJobIds.Contains(pendingJobId.Value);

    private static void ClearFile(FileEntry file)
    {
        file.PendingName = null;
        file.PendingDirectoryId = null;
        file.PendingState = EntityPendingState.None;
        file.PendingJobId = null;
    }

    private static void ClearDirectory(DirectoryNode directory)
    {
        directory.PendingName = null;
        directory.PendingParentId = null;
        directory.PendingState = EntityPendingState.None;
        directory.PendingJobId = null;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-reads the given files from the database into the FTS index. Set-based and always the
    /// same statement, so the projected name is computed in exactly one place
    /// (<see cref="Projected"/> documents where).
    /// </summary>
    private async Task SyncFtsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0) return;
        await _fts.SyncFilesAsync(fileIds, ct);
    }

    private async Task<FileEntry?> LoadSourceFileAsync(
        OperationJob job, IReadOnlyCollection<OperationJobItem> items, CancellationToken ct)
    {
        var fileId = items.FirstOrDefault(i => i.FileId.HasValue)?.FileId;
        if (fileId is null)
        {
            _logger.LogWarning("Job {Id} ({Type}): no item carries a FileId — no overlay written.",
                job.Id, job.Type);
            return null;
        }

        var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == fileId.Value, ct);
        if (file is null)
            _logger.LogWarning("Job {Id} ({Type}): file {FileId} is gone — no overlay written.",
                job.Id, job.Type, fileId.Value);
        return file;
    }

    /// <summary>
    /// The source directory of a folder operation. Both RenameFolder and MoveFolder carry it on
    /// their single <c>FileId = null</c> item (for MoveFolder that is the folder marker the engine
    /// also uses as the subtree root), so the row is found by volume + materialized path — which
    /// the overlay never changes, so this still resolves on a retry.
    /// </summary>
    private async Task<DirectoryNode?> LoadSourceDirectoryAsync(
        OperationJob job, IReadOnlyCollection<OperationJobItem> items, CancellationToken ct)
    {
        var sourcePath = items.FirstOrDefault(i => i.FileId is null)?.SourceRelativePath;
        if (sourcePath is null || job.SourceVolumeId is null)
        {
            _logger.LogWarning(
                "Job {Id} ({Type}): no folder item to resolve the source directory — no overlay written.",
                job.Id, job.Type);
            return null;
        }

        var dir = await _db.Directories.FirstOrDefaultAsync(
            d => d.VolumeId == job.SourceVolumeId.Value && d.MaterializedPath == sourcePath, ct);
        if (dir is null)
            _logger.LogWarning(
                "Job {Id} ({Type}): no Directories row at '{Path}' on volume {Vol} — no overlay written.",
                job.Id, job.Type, sourcePath, job.SourceVolumeId.Value);
        return dir;
    }
}
