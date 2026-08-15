using FileTracert.Business.Projection;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Everything a parked job needs before it may run again, in one place, so the two callers that
/// release a job — the automatic revaluation and the user's «Riprova» — cannot drift apart:
///
/// <list type="number">
///   <item><b>Ask the guard again.</b> The world moved while the job waited; another job may now
///     be in the way. Re-asking is what makes the single <c>DependsOnJobId</c> enough (§2.2): the
///     dependency is re-pointed, never assumed still valid.</item>
///   <item><b>Refresh the snapshots.</b> The paths it was queued with may name places that no
///     longer exist (<see cref="JobSnapshotRefresher"/>).</item>
///   <item><b>Take the overlay.</b> A job that was waiting owned no projection; the moment it
///     becomes the queue's promise for that entity, the Catalog has to say so.</item>
/// </list>
///
/// The caller saves and commits — release, overlay and (where applicable) the ledger have to
/// reach the database in one transaction or a crash leaves a job runnable with no projection.
/// </summary>
public sealed class JobUnblocker
{
    private readonly FileTracertDbContext _db;
    private readonly PendingWorkGuard _guard;
    private readonly OverlayWriter _overlay;
    private readonly JobSnapshotRefresher _snapshots;
    private readonly ILogger<JobUnblocker> _logger;

    public JobUnblocker(
        FileTracertDbContext db,
        PendingWorkGuard guard,
        OverlayWriter overlay,
        JobSnapshotRefresher snapshots,
        ILogger<JobUnblocker> logger)
    {
        _db = db;
        _guard = guard;
        _overlay = overlay;
        _snapshots = snapshots;
        _logger = logger;
    }

    /// <summary>
    /// The job in the queue that currently stands in the way of <paramref name="job"/>, or null.
    /// Excludes the job itself — it is already in the queue, and it is not its own obstacle.
    /// </summary>
    public async Task<PendingConflict?> FindConflictAsync(OperationJob job, CancellationToken ct)
    {
        List<OperationJobItem> items = job.Items.Count > 0
            ? [.. job.Items]
            : await _db.OperationJobItems.AsNoTracking()
                .Where(i => i.JobId == job.Id).ToListAsync(ct);

        var claims = PendingWorkGuard.ClaimsOf(
            job.Type, job.SourceVolumeId, job.TargetVolumeId, job.TargetRelativePath, items);

        // Only what is AHEAD of it in the queue: a job already in the queue can never be made to
        // wait for one enqueued after it, or two overlapping jobs would each wait for the other.
        return await _guard.FindConflictAsync(
            claims, excludeJobId: job.Id, ct, beforeSequenceOrder: job.SequenceOrder);
    }

    /// <summary>
    /// Rewrites the job's path snapshots to the world as it is now — WITHOUT saving, so a caller
    /// that then decides not to release the job can discard the edits. Returns null when the job
    /// is ready, or an Italian description of what could not be resolved; the caller keeps it
    /// <c>Blocked</c> with that message. Never <c>Failed</c>: an entity that vanished can come
    /// back (a re-scan, a remounted drive), so this is a parking condition, not a verdict.
    ///
    /// Idempotent — refreshing already-fresh snapshots rewrites the same strings.
    /// </summary>
    public async Task<string?> RefreshSnapshotsAsync(OperationJob job, CancellationToken ct)
    {
        var problem = await _snapshots.RefreshAsync(job, ct);
        if (problem is not null)
            _logger.LogWarning("Job {Id}: cannot be released — {Problem}", job.Id, problem);
        return problem;
    }

    /// <summary>
    /// Stamps the projection overlay for a job that has just become the queue's promise for its
    /// entity. Must run inside the transaction that commits the state change: a runnable job with
    /// no overlay is an operation the Catalog and the search cannot see.
    ///
    /// Idempotent — a job that already owns its overlay gets the same values re-stamped.
    /// </summary>
    public Task TakeOverlayAsync(OperationJob job, CancellationToken ct) =>
        _overlay.ApplyAsync(job, [.. job.Items], ct);
}
