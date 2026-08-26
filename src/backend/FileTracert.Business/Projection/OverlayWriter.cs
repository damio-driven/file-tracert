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
            case JobType.CopyFile:
                await ApplyCopyFileAsync(job, items, ct);
                break;
            case JobType.CopyFolder:
                await ApplyCopyFolderAsync(job, items, ct);
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

    /// <summary>
    /// A copy is the ONE queued operation whose result is a new entity, so §5's «queuing mutates
    /// the projection immediately» cannot be expressed with <c>Pending*</c> fields: there is no
    /// existing row at the destination to carry them. The destination row is created ahead of the
    /// file instead — <c>IsMaterialized = false</c>, <c>IsPresent = false</c>,
    /// <see cref="EntityPendingState.PendingCreate"/> — which is exactly the job
    /// <c>IsMaterialized</c> has always done for directories (step 15a).
    ///
    /// <para>The SOURCE row is not touched at all, and that is the point: a copy leaves it where
    /// it is, so stamping an overlay on it would promise the user a change to a file that is not
    /// changing.</para>
    /// </summary>
    private async Task ApplyCopyFileAsync(
        OperationJob job, IReadOnlyCollection<OperationJobItem> items, CancellationToken ct)
    {
        var source = await LoadSourceFileAsync(job, items, ct);
        if (source is null || job.TargetVolumeId is null) return;

        var targetDir = await _directories.FindOrCreateProjectedAsync(
            job.TargetVolumeId.Value, ScanPath.Parent(job.TargetRelativePath!), job.Id, ct);

        var projected = await ProjectCopiesAsync(
            job, [(source, targetDir, ScanPath.Name(job.TargetRelativePath!))], ct);

        // §5 — the projected name is what gets indexed, so the copy is findable the moment it is
        // queued rather than only once the bytes land.
        await SyncFtsAsync([.. projected.Select(r => r.Id)], ct);
    }

    /// <summary>
    /// The folder copy projects one row per file it is going to write, plus the destination
    /// directory tree. It cannot do what <see cref="ApplyMoveFolderAsync"/> does — one overlay on
    /// the folder row, with the descendants' projected paths falling out of the parent walk —
    /// because none of those descendants exist at the destination yet.
    ///
    /// <para>Cost, stated rather than discovered later: one inserted row per expanded item. The
    /// enqueue is already writing one <see cref="OperationJobItem"/> per file in the same
    /// transaction, so this is the same order of work, not a new class of it; the batch ceiling of
    /// <c>QueueService.MaxBatchSize</c> is what bounds the gesture.</para>
    /// </summary>
    private async Task ApplyCopyFolderAsync(
        OperationJob job, IReadOnlyCollection<OperationJobItem> items, CancellationToken ct)
    {
        if (job.TargetVolumeId is null || string.IsNullOrEmpty(job.TargetRelativePath)) return;
        var targetVolumeId = job.TargetVolumeId.Value;

        // The destination root always exists in the projection, even for a folder whose subtree
        // holds no indexed file (C21's shape): the job does create it.
        await _directories.FindOrCreateProjectedAsync(targetVolumeId, job.TargetRelativePath, job.Id, ct);

        var fileItems = items.Where(i => i.FileId.HasValue).ToList();
        if (fileItems.Count == 0)
        {
            await _db.SaveChangesAsync(ct);
            return;
        }

        var sourceIds = fileItems.Select(i => i.FileId!.Value).ToList();
        var sources = await _db.Files
            .Where(f => sourceIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        // One resolve per distinct destination directory: a folder copy lands thousands of files
        // into a handful of them.
        var dirCache = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(FileEntry Source, DirectoryNode TargetDir, string Name)>(fileItems.Count);

        foreach (var item in fileItems)
        {
            if (!sources.TryGetValue(item.FileId!.Value, out var source)) continue;

            var dirPath = ScanPath.Parent(item.TargetRelativePath);
            if (!dirCache.TryGetValue(dirPath, out var targetDir))
            {
                targetDir = await _directories.FindOrCreateProjectedAsync(
                    targetVolumeId, dirPath, job.Id, ct);
                dirCache[dirPath] = targetDir;
            }

            plan.Add((source, targetDir, ScanPath.Name(item.TargetRelativePath)));
        }

        var projected = await ProjectCopiesAsync(job, plan, ct);
        await SyncFtsAsync([.. projected.Select(r => r.Id)], ct);
    }

    /// <summary>
    /// The destination rows of a batch of copied files. Returns them (existing or new) so the
    /// caller can index them.
    ///
    /// <para><b>One query and one save for the whole batch</b>, not per file. This runs inside the
    /// enqueue's transaction, which holds SQLite's single write lock, and a folder copy expands to
    /// one item per file in the subtree: a lookup plus a <c>SaveChanges</c> each would put
    /// thousands of round trips under that lock, on the path where a copying job's own checkpoints
    /// are waiting for it. The rows are added first and their ids read after the single save.</para>
    ///
    /// <para>Idempotent like the rest of <see cref="ApplyAsync"/> — a retry re-runs it and must
    /// find its own rows rather than add a second set, so rows this job already owns at these
    /// places are reused.</para>
    ///
    /// <para>What is copied from the source and what is NOT: name, size, dates and attributes are
    /// what the copy is going to produce, so they describe the promise honestly. The hashes are
    /// left null even though the content will be identical — claiming a hash for a file nothing
    /// has written yet is a lie a verifier could act on. <c>UsnFileRef</c> is null for the same
    /// reason and one more: it is the FRN of a file that does not exist, and the unique
    /// <c>(VolumeId, UsnFileRef)</c> index is filtered, so nulls coexist.</para>
    ///
    /// <para><c>IsIncluded</c> and its three causes are inherited from the source as a
    /// PROVISIONAL value. The copy keeps the name, so the type verdict is right; the root and
    /// perimeter verdicts belong to the destination and are recomputed by <c>IndexUpdater</c> when
    /// the job completes. Deciding them here would mean giving this class the filter resolution,
    /// which is not its business — and nothing depends on the guess, because the row's visibility
    /// in the Catalog and in the search index comes from <c>IsMaterialized = false</c>, not from
    /// inclusion.</para>
    /// </summary>
    private async Task<IReadOnlyList<FileEntry>> ProjectCopiesAsync(
        OperationJob job,
        IReadOnlyList<(FileEntry Source, DirectoryNode TargetDir, string Name)> plan,
        CancellationToken ct)
    {
        if (plan.Count == 0) return [];

        // Everything this job may already have projected, in ONE query — the retry case. Scoped by
        // DIRECTORY and not by PendingJobId alone: PendingJobId carries no index, so the plain
        // form is a scan of Files (742 033 rows on the real catalog) run inside the enqueue's
        // write transaction, and for a first application it would scan all of that to find
        // nothing. DirectoryId is the leading column of the covering indexes of 11e/14c, so this
        // is a handful of seeks over the destination folders instead.
        var targetDirIds = plan.Select(x => x.TargetDir.Id).Distinct().ToList();
        var owned = await _db.Files
            .Where(f => targetDirIds.Contains(f.DirectoryId) && f.PendingJobId == job.Id)
            .ToListAsync(ct);
        var existingByPlace = new Dictionary<(int, string), FileEntry>(PlaceComparer.Instance);
        foreach (var row in owned)
            existingByPlace.TryAdd((row.DirectoryId, row.Name), row);

        var result = new List<FileEntry>(plan.Count);

        foreach (var (source, targetDir, name) in plan)
        {
            if (string.IsNullOrEmpty(name))
            {
                _logger.LogWarning(
                    "Job {Id} ({Type}): a copy item has no destination file name — no projection row created.",
                    job.Id, job.Type);
                continue;
            }

            if (existingByPlace.TryGetValue((targetDir.Id, name), out var already))
            {
                result.Add(already);
                continue;
            }

            var created = NewProjectedCopy(source, targetDir, name, job);
            _db.Files.Add(created);
            existingByPlace[(targetDir.Id, name)] = created;
            result.Add(created);
        }

        // The single save of the batch: every id below is assigned here.
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Case-insensitive on the name, the way this catalog compares file names everywhere.</summary>
    private sealed class PlaceComparer : IEqualityComparer<(int DirectoryId, string Name)>
    {
        public static readonly PlaceComparer Instance = new();

        public bool Equals((int DirectoryId, string Name) a, (int DirectoryId, string Name) b) =>
            a.DirectoryId == b.DirectoryId &&
            string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((int DirectoryId, string Name) x) =>
            HashCode.Combine(x.DirectoryId, x.Name.ToUpperInvariant());
    }

    private static FileEntry NewProjectedCopy(
        FileEntry source, DirectoryNode targetDir, string name, OperationJob job)
    {
        return new FileEntry
        {
            VolumeId = targetDir.VolumeId,
            DirectoryId = targetDir.Id,
            Name = name,
            Extension = source.Extension,
            Category = source.Category,
            SizeBytes = source.SizeBytes,
            FileCreatedUtc = source.FileCreatedUtc,
            FileModifiedUtc = source.FileModifiedUtc,
            Attributes = source.Attributes,
            UsnFileRef = null,
            IsIncluded = source.IsIncluded,
            ExcludedByType = source.ExcludedByType,
            ExcludedByRoot = source.ExcludedByRoot,
            ExcludedByScan = source.ExcludedByScan,
            IsMaterialized = false,
            IsPresent = false,
            LastIndexedUtc = DateTime.UtcNow,
            PendingState = EntityPendingState.PendingCreate,
            PendingJobId = job.Id,
        };
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
    ///
    /// <para><b>The one place §6's no-hard-delete does not apply</b> (step 15a). A cancelled or
    /// failed Copy leaves a destination row that was never a file: nothing ever created it, no
    /// scan ever saw it, and it describes nothing on any disk. Blanking its <c>Pending*</c> fields
    /// the way every other operation's cleanup does would leave that nothing behind for ever, as a
    /// row with <c>IsMaterialized = false</c> and no owner. The rule it is exempt from exists to
    /// protect facts about the disk from being erased; this row is not one. It is told apart by
    /// <c>IsMaterialized = false</c>, which only <see cref="ProjectCopyAsync"/> ever writes.</para>
    /// </summary>
    public async Task ClearForJobAsync(int jobId, CancellationToken ct)
    {
        var files = await _db.Files.Where(f => f.PendingJobId == jobId).ToListAsync(ct);
        var directories = await _db.Directories.Where(d => d.PendingJobId == jobId).ToListAsync(ct);

        if (files.Count == 0 && directories.Count == 0) return;

        // Captured BEFORE the delete: the search entries are keyed by rowid and have to be pruned
        // by id, which the removed entities no longer answer for once they are gone.
        var touchedFileIds = files.Select(f => f.Id).ToList();

        var (projected, overlaid) = SplitProjected(files);
        _db.Files.RemoveRange(projected);
        foreach (var file in overlaid) ClearFile(file);
        foreach (var dir in directories) ClearDirectory(dir);
        await _db.SaveChangesAsync(ct);

        // The FTS name column followed the overlay on the way in — it must follow it back out, or
        // a cancelled rename stays findable under a name that was never applied. The same call
        // prunes the deleted rows: it DELETEs by rowid and then re-inserts only what Files still
        // holds, so an id that no longer exists simply loses its entry.
        await SyncFtsAsync(touchedFileIds, ct);

        _logger.LogDebug(
            "Job {Id}: overlay cleared on {Files} file(s) and {Dirs} directory(ies); " +
            "{Projected} never-created destination row(s) removed.",
            jobId, overlaid.Count, directories.Count, projected.Count);
    }

    /// <summary>
    /// Splits the rows a job owns into the ones that stand for a real file — whose overlay is
    /// blanked — and the ones a Copy invented at its destination, which are deleted. See
    /// <see cref="ClearForJobAsync"/> for why the second half is not a violation of §6.
    /// </summary>
    private static (List<FileEntry> Projected, List<FileEntry> Overlaid) SplitProjected(
        List<FileEntry> files)
    {
        List<FileEntry> projected = [];
        List<FileEntry> overlaid = [];
        foreach (var file in files)
        {
            if (file.IsMaterialized) overlaid.Add(file);
            else projected.Add(file);
        }
        return (projected, overlaid);
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

        var touchedFileIds = orphanFiles.Select(f => f.Id).ToList();

        // Same split as ClearForJobAsync, for the same reason: an orphaned Copy destination row is
        // a promise whose job is gone, and it never stood for a file.
        var (projected, overlaid) = SplitProjected(orphanFiles);
        _db.Files.RemoveRange(projected);
        foreach (var file in overlaid) ClearFile(file);
        foreach (var dir in orphanDirs) ClearDirectory(dir);
        await _db.SaveChangesAsync(ct);
        await SyncFtsAsync(touchedFileIds, ct);

        _logger.LogWarning(
            "Startup reconciliation: cleared {Files} orphan file overlay(s) and {Dirs} orphan " +
            "directory overlay(s) whose job no longer exists or is already terminal, and removed " +
            "{Projected} never-created copy destination row(s).",
            overlaid.Count, orphanDirs.Count, projected.Count);

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
