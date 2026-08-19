using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;
using FileTracert.Data.Entities;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Brings a parked job's path snapshots back up to date before it is allowed to run
/// (finding 8a — the real one).
///
/// <c>OperationJobItem.SourceRelativePath</c> is a snapshot taken at enqueue. A job that waited
/// behind another one waited precisely because that other job was about to move the ground under
/// it: by the time it is released, "Docs\report.txt" may be "Documenti\report.txt". Running on the
/// stale string produced a <c>FileNotFoundException</c> → <c>Failed</c>, permanently, because the
/// retry re-used the same dead snapshot.
///
/// Two sources of truth, in this order:
/// <list type="number">
///   <item><b>Identity.</b> An item with a <c>FileId</c> is re-read from the catalog: the row
///     survives re-scans (step 9a) and every completed job, so its current directory + name IS
///     the current path, however many jobs ran in between.</item>
///   <item><b>Replay.</b> A folder item has no such handle, so the folder-level transformations
///     that COMPLETED after this job was queued are replayed over its path as prefix rewrites.
///     Same for destination paths, which no row identifies.</item>
/// </list>
///
/// <para><b>Known limit, deliberate:</b> the replay only covers intra-volume folder operations.
/// A folder moved to ANOTHER volume takes its content with it, so a path under it is not stale,
/// it is gone — and that is reported as a problem (the job stays <c>Blocked</c> with an explicit
/// message) rather than silently rewritten to somewhere it never was.</para>
/// </summary>
public sealed class JobSnapshotRefresher
{
    private readonly FileTracertDbContext _db;
    private readonly ILogger<JobSnapshotRefresher> _logger;

    public JobSnapshotRefresher(FileTracertDbContext db, ILogger<JobSnapshotRefresher> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>A folder operation that already ran: everything under <c>From</c> now lives under <c>To</c>.</summary>
    private readonly record struct FolderMove(int VolumeId, string From, string To);

    /// <summary>
    /// Rewrites the job's snapshots in place (the caller saves). Returns null when the job is
    /// ready to run, or an Italian description of what could not be resolved — which the caller
    /// turns into a <c>Blocked</c> with a reason, never a silent <c>Failed</c>.
    /// </summary>
    public async Task<string?> RefreshAsync(OperationJob job, CancellationToken ct)
    {
        var moves = await LoadFolderMovesSinceAsync(job, ct);

        // Where the files this job owns actually live now. A path is only half of "where": the
        // other half is the volume, and a job released after a CROSS-volume move used to keep
        // naming the drive its file had left (see FollowSourceVolumeAsync).
        var volumes = new HashSet<int>();

        // Only items that have not started: a job parked mid-flight (space, collision) keeps
        // copies already on the target, and their paths are history, not a plan.
        foreach (var item in job.Items.Where(i => i.State == JobItemState.Pending))
        {
            var problem = await RefreshItemAsync(job, item, moves, volumes, ct);
            if (problem is not null) return problem;
        }

        var moved = await FollowSourceVolumeAsync(job, volumes, ct);
        if (moved is not null) return moved;

        // The job-level destination is what the engine uses to create the target folder; it must
        // follow the same rewrites, or a completed rename resurrects the old folder name.
        if (job.Type is JobType.MoveFile or JobType.MoveFolder or JobType.CreateFolder &&
            !string.IsNullOrEmpty(job.TargetRelativePath))
        {
            job.TargetRelativePath = Replay(moves, job.TargetVolumeId, job.TargetRelativePath);
        }

        return null;
    }

    /// <summary>
    /// Re-points the job at the volume its files are on NOW.
    ///
    /// <para>The harness FAIL of step 9c on a cross-volume pair. Resolving an item by identity
    /// gives the right PATH however many jobs ran in between, but the job's own
    /// <c>SourceVolumeId</c> was left at the drive the file was queued from. After a cross-volume
    /// move the engine then looked for the file on the wrong drive, threw a plain IOException and
    /// the job went <c>Failed</c> — terminal, for an operation that is perfectly runnable one
    /// drive over. The dependent has to follow its file, which is also what the user expects.</para>
    ///
    /// <para>The job's SHAPE is never changed here, only the drive it names:</para>
    /// <list type="bullet">
    /// <list type="bullet">
    ///   <item>a RENAME derives its destination from the refreshed source (see
    ///     <see cref="RefreshItemAsync"/>), so both ends travel with the file and the operation
    ///     stays the one the user asked for. It needs no reservation either way;</item>
    ///   <item>a MOVE's destination is a place the user picked, on a volume they picked — it
    ///     follows the source nowhere. A cross-volume move whose new source is still not the
    ///     destination keeps that destination and stays cross; the callers
    ///     (<c>BlockedJobRevaluator.UnblockAsync</c>, <c>QueueService.RetryAsync</c>) re-run
    ///     release-then-reserve straight after this, so the ledger's liberation entry follows the
    ///     new source volume by itself;</item>
    ///   <item>every other move is reported as a problem instead of being rewritten. A move whose
    ///     source has landed ON its own destination would stop being a copy at all; an
    ///     <b>intra-volume</b> move whose file has left that volume would silently land on a drive
    ///     the user never chose, because its <c>TargetRelativePath</c> is a path they picked on the
    ///     old volume and nothing in it names a drive. Both stay <c>Blocked</c> with a message the
    ///     user can act on — §4, a recoverable condition, never <c>Failed</c>.</item>
    /// </list>
    /// </summary>
    private async Task<string?> FollowSourceVolumeAsync(
        OperationJob job, HashSet<int> resolvedVolumes, CancellationToken ct)
    {
        // Nothing resolved by identity (folder items, CreateFolder): the replay is the only tool
        // available, and it deliberately does not cross volumes.
        if (resolvedVolumes.Count == 0) return null;

        if (resolvedVolumes.Count > 1)
            return "I file dell'operazione risultano ora su volumi diversi: l'operazione resta " +
                   "in attesa finché non tornano insieme o non viene annullata.";

        var volumeId = resolvedVolumes.Single();
        if (job.SourceVolumeId == volumeId) return null;

        var isRename = job.Type is JobType.RenameFile or JobType.RenameFolder;
        if (!isRename)
        {
            // A move keeps the destination the user chose. If the file is already there, the move
            // has nothing left to do; if the move was planned WITHIN the volume the file has just
            // left, its destination path names a place on that old drive and following the file
            // would drop it somewhere nobody asked for.
            if (job.TargetVolumeId == volumeId)
                return $"Il file è stato spostato sul volume di destinazione ({volumeId}) da " +
                       "un'altra operazione: lo spostamento richiesto non ha più senso. " +
                       "Annullare l'operazione o riprovarla verso un'altra destinazione.";

            if (job.IsIntraVolume)
                return $"Il file non si trova più sul volume ({job.SourceVolumeId}) su cui questo " +
                       $"spostamento era stato pianificato, ma sul volume {volumeId}: la " +
                       "destinazione scelta non esiste più dove doveva. Annullare l'operazione o " +
                       "riprovarla scegliendo di nuovo la destinazione.";
        }

        _logger.LogInformation(
            "Job {Id}: its file(s) now live on volume {New} instead of {Old} — following them.",
            job.Id, volumeId, job.SourceVolumeId);

        job.SourceVolumeId = volumeId;
        // Loaded rather than left stale: the FK and the navigation must agree, or a caller that
        // reads job.SourceVolume after this (a label, a log line) describes the old drive.
        job.SourceVolume = await _db.Volumes.FirstOrDefaultAsync(v => v.Id == volumeId, ct);

        // A rename happens where the file is: both ends of an in-place operation travel together.
        job.TargetVolumeId = volumeId;
        job.TargetVolume = job.SourceVolume;

        return null;
    }

    private async Task<string?> RefreshItemAsync(
        OperationJob job, OperationJobItem item, List<FolderMove> moves,
        HashSet<int> resolvedVolumes, CancellationToken ct)
    {
        if (item.FileId is { } fileId)
        {
            var file = await _db.Files.Include(f => f.Directory).AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == fileId, ct);

            if (file is null)
                return $"Il file dell'operazione (id {fileId}) non è più nel catalogo: " +
                       "l'operazione resta in attesa finché non ricompare o non viene annullata.";

            if (!file.IsPresent)
                _logger.LogWarning(
                    "Job {Id}: file {FileId} is flagged absent — the operation will be attempted " +
                    "anyway and the engine will report if it really is gone.", job.Id, fileId);

            item.SourceRelativePath = ScanPath.Join(file.Directory.MaterializedPath, file.Name);
            resolvedVolumes.Add(file.VolumeId);
        }
        else
        {
            var source = Replay(moves, job.SourceVolumeId, item.SourceRelativePath);

            // Only a path the replay actually REWROTE is second-guessed: that value is inferred,
            // so it must correspond to a real row. An untouched snapshot is left exactly as the
            // enqueue wrote it — validating it here would reject jobs whose folder was never in
            // the catalog, which is not this method's business.
            if (!ScanPath.SamePath(source, item.SourceRelativePath) &&
                job.SourceVolumeId is { } volumeId &&
                !await _db.Directories.AnyAsync(
                    d => d.VolumeId == volumeId && d.MaterializedPath == source, ct))
            {
                return $"La cartella di origine '{item.SourceRelativePath}' è stata spostata da " +
                       $"un'altra operazione e non è risolvibile in '{source}': l'operazione " +
                       "resta in attesa finché la cartella non ricompare o non viene annullata.";
            }

            item.SourceRelativePath = source;
        }

        // A rename keeps the entity where it is and only changes the last segment, so its target
        // is derived from the refreshed source — replaying it would be a second, redundant rule.
        item.TargetRelativePath = job.Type is JobType.RenameFile or JobType.RenameFolder
            ? ScanPath.Join(ScanPath.Parent(item.SourceRelativePath), ScanPath.Name(item.TargetRelativePath))
            : Replay(moves, job.TargetVolumeId, item.TargetRelativePath);

        return null;
    }

    /// <summary>
    /// The intra-volume folder operations that completed AFTER this job was queued — i.e. exactly
    /// the ones its snapshots cannot know about — in the order they were applied. Folder items
    /// only (<c>FileId is null</c>): a completed FILE operation moves nothing but itself, and
    /// files are re-resolved by identity anyway.
    /// </summary>
    private async Task<List<FolderMove>> LoadFolderMovesSinceAsync(OperationJob job, CancellationToken ct)
    {
        var completed = await _db.OperationJobs.AsNoTracking()
            .Where(j => j.Id != job.Id &&
                        j.State == JobState.Completed &&
                        j.CompletedUtc != null && j.CompletedUtc > job.CreatedUtc &&
                        (j.Type == JobType.RenameFolder || j.Type == JobType.MoveFolder) &&
                        j.SourceVolumeId != null && j.SourceVolumeId == j.TargetVolumeId)
            .OrderBy(j => j.CompletedUtc)
            .Select(j => new { j.Id, VolumeId = j.SourceVolumeId!.Value })
            .ToListAsync(ct);

        if (completed.Count == 0) return [];

        var ids = completed.Select(c => c.Id).ToList();
        var markers = await _db.OperationJobItems.AsNoTracking()
            .Where(i => ids.Contains(i.JobId) && i.FileId == null)
            .Select(i => new { i.JobId, i.SourceRelativePath, i.TargetRelativePath })
            .ToListAsync(ct);

        var byJob = markers.GroupBy(m => m.JobId).ToDictionary(g => g.Key, g => g.First());

        var moves = new List<FolderMove>();
        foreach (var job2 in completed)
        {
            if (!byJob.TryGetValue(job2.Id, out var marker)) continue;
            if (ScanPath.SamePath(marker.SourceRelativePath, marker.TargetRelativePath)) continue;
            moves.Add(new FolderMove(job2.VolumeId, marker.SourceRelativePath, marker.TargetRelativePath));
        }

        if (moves.Count > 0)
            _logger.LogDebug(
                "Job {Id}: replaying {Count} folder move(s) completed since it was queued.",
                job.Id, moves.Count);

        return moves;
    }

    /// <summary>
    /// Applies the prefix rewrites in completion order. Idempotent in practice: once a path has
    /// been rewritten it no longer sits under the old root, so a second pass changes nothing.
    /// </summary>
    private static string Replay(IReadOnlyList<FolderMove> moves, int? volumeId, string path)
    {
        if (volumeId is null || string.IsNullOrEmpty(path)) return path;

        foreach (var move in moves)
        {
            if (move.VolumeId != volumeId.Value) continue;
            if (!ScanPath.IsWithin(path, move.From)) continue;
            path = move.To + path[move.From.Length..];
        }

        return path;
    }
}
