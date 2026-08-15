using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
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

        // Only items that have not started: a job parked mid-flight (space, collision) keeps
        // copies already on the target, and their paths are history, not a plan.
        foreach (var item in job.Items.Where(i => i.State == JobItemState.Pending))
        {
            var problem = await RefreshItemAsync(job, item, moves, ct);
            if (problem is not null) return problem;
        }

        // The job-level destination is what the engine uses to create the target folder; it must
        // follow the same rewrites, or a completed rename resurrects the old folder name.
        if (job.Type is JobType.MoveFile or JobType.MoveFolder or JobType.CreateFolder &&
            !string.IsNullOrEmpty(job.TargetRelativePath))
        {
            job.TargetRelativePath = Replay(moves, job.TargetVolumeId, job.TargetRelativePath);
        }

        return null;
    }

    private async Task<string?> RefreshItemAsync(
        OperationJob job, OperationJobItem item, List<FolderMove> moves, CancellationToken ct)
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
