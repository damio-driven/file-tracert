using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Operations;

/// <summary>A volume-scoped path a job lays claim to. The same relative path on another volume is another place.</summary>
/// <param name="VolumeId">Volume the path belongs to.</param>
/// <param name="Path">Volume-relative path (normalized, no leading/trailing separator).</param>
/// <param name="IsSource">
/// True when the job is going to take this path AWAY (rename it, move it out of there); false when
/// the job is going to put something THERE. The distinction is what tells a real conflict from the
/// §5 case "queue the folder X, then queue files into it" — see <see cref="PendingWorkGuard"/>.
/// </param>
public readonly record struct PathClaim(int VolumeId, string Path, bool IsSource);

/// <summary>A non-terminal job already holding a path the new operation wants.</summary>
public sealed record PendingConflict(int JobId, int SequenceOrder, JobType Type, string Path);

/// <summary>
/// The ONE place that answers: «does this new operation touch something a job already in the
/// queue is touching?» (finding 8 + K5).
///
/// Before this class the question was asked in two half-blind ways — one query matched a single
/// <c>FileId</c>, the other only source paths on the source volume — so three shapes of conflict
/// went through undetected (a folder op invalidating a file op inside it, a rename hitting the
/// destination of a pending move, and <c>CreateFolder</c>, which owns no item at all and was
/// therefore invisible).
///
/// <para><b>The rule.</b> Two jobs conflict when:</para>
/// <list type="bullet">
///   <item>a SOURCE path of one overlaps ANY path of the other — a job that renames or moves a
///     path pulls the ground from under anything at, above or below it; or</item>
///   <item>two TARGET paths are EQUAL — two operations landing on the very same destination.</item>
/// </list>
/// Two targets that merely nest do NOT conflict: that is exactly §5's «I queue folder X and then
/// move files into it», which must stay legal — the second job resolves X in the projection.
///
/// <para><b>Known limit, accepted.</b> A folder job is represented by its root marker, which
/// covers every ancestor/descendant question but NOT the exact-target one: a move landing on the
/// very path one of a pending <c>MoveFolder</c>'s expanded file items will occupy is compared
/// against the folder's root, not that leaf, and goes through as <c>Pending</c>. It is caught at
/// execution as <c>Blocked(NameCollision)</c> — recoverable, and nothing is ever overwritten,
/// because <c>FinalizePartial</c> refuses an existing target. Closing it would mean loading every
/// expanded item of every pending folder job (a cross-volume move of 100 000 files) on each
/// enqueue, which is a bad trade for a case the engine already parks safely.</para>
///
/// <para><b>Where the predicate lives.</b> Overlap is decided in memory by
/// <see cref="ScanPath.Overlaps"/>, the single case-insensitive, segment-aware definition. The SQL
/// side only narrows the candidates (non-terminal jobs on the volumes involved). That is a
/// deliberate deviation from «the predicate in two forms»: a SQL form would compare with SQLite's
/// LIKE/BINARY mix and could not agree with the in-memory one — the divergence K5 is about. The
/// non-terminal queue is user-scale (tens of jobs), so narrowing then deciding in memory is
/// bounded, and it cannot drift.</para>
/// </summary>
public sealed class PendingWorkGuard
{
    private readonly FileTracertDbContext _db;

    public PendingWorkGuard(FileTracertDbContext db) => _db = db;

    // ── claims ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The paths a job lays claim to. A <c>MoveFolder</c> is represented by its folder marker
    /// item alone: every file item of a cross-volume expansion sits below that marker's source
    /// and target roots, so the roots already cover them and a 100 000-file job stays a two-claim
    /// job. <c>CreateFolder</c> owns no item at all — its only path is on the job itself.
    /// </summary>
    public static List<PathClaim> ClaimsOf(
        JobType type, int? sourceVolumeId, int? targetVolumeId, string? targetRelativePath,
        IReadOnlyCollection<OperationJobItem> items)
    {
        var claims = new List<PathClaim>();

        if (type == JobType.CreateFolder)
        {
            Add(claims, targetVolumeId, targetRelativePath, isSource: false);
            return claims;
        }

        // Folder ops carry a FileId-less marker item that IS the folder; when one exists it
        // subsumes the expanded file items. Jobs written before the marker existed have none —
        // fall back to every item, which is a superset and therefore still safe.
        var markers = items.Where(i => i.FileId is null).ToList();
        var representative = markers.Count > 0 ? markers : items;

        foreach (var item in representative)
        {
            Add(claims, sourceVolumeId, item.SourceRelativePath, isSource: true);
            Add(claims, targetVolumeId, item.TargetRelativePath, isSource: false);
        }

        return claims;
    }

    private static void Add(List<PathClaim> claims, int? volumeId, string? path, bool isSource)
    {
        if (volumeId is null || string.IsNullOrEmpty(path)) return;
        claims.Add(new PathClaim(volumeId.Value, path, isSource));
    }

    /// <summary>
    /// The conflict rule, stated once. Symmetric by construction: it is asked in both directions
    /// so «my source under your folder» and «your folder over my source» are the same answer.
    /// </summary>
    public static bool Conflicts(IReadOnlyCollection<PathClaim> a, IReadOnlyCollection<PathClaim> b) =>
        SourceOverlapsAnything(a, b) || SourceOverlapsAnything(b, a) || SameTarget(a, b);

    private static bool SourceOverlapsAnything(
        IReadOnlyCollection<PathClaim> sources, IReadOnlyCollection<PathClaim> others) =>
        sources.Where(s => s.IsSource).Any(s => others.Any(o =>
            o.VolumeId == s.VolumeId && ScanPath.Overlaps(s.Path, o.Path)));

    private static bool SameTarget(IReadOnlyCollection<PathClaim> a, IReadOnlyCollection<PathClaim> b) =>
        a.Where(x => !x.IsSource).Any(x => b.Any(y =>
            !y.IsSource && y.VolumeId == x.VolumeId && ScanPath.SamePath(x.Path, y.Path)));

    // ── lookup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The non-terminal job that stands in the way of <paramref name="claims"/>, or null when the
    /// path is clear. When several jobs conflict — possible once subtrees are involved — the one
    /// LAST in queue order is returned: everything ahead of it resolves first anyway, and the
    /// revaluation re-asks this question before actually unblocking anybody.
    /// </summary>
    /// <param name="excludeJobId">The job asking, when it is already in the queue.</param>
    /// <param name="beforeSequenceOrder">
    /// Only jobs AHEAD of this position count. Null while enqueueing (the new job goes last, so
    /// everything is ahead of it). Passing it on a re-ask is what keeps the queue a queue: a job
    /// can only ever wait for something in front of it, so two jobs that overlap can never end up
    /// waiting for each other — which, without this, is a permanent deadlock of both.
    /// </param>
    public async Task<PendingConflict?> FindConflictAsync(
        IReadOnlyCollection<PathClaim> claims, int? excludeJobId, CancellationToken ct,
        int? beforeSequenceOrder = null)
    {
        if (claims.Count == 0) return null;

        var volumeIds = claims.Select(c => c.VolumeId).Distinct().ToList();

        var heads = await _db.OperationJobs
            .Where(j => !JobStates.Terminal.Contains(j.State) &&
                        (excludeJobId == null || j.Id != excludeJobId) &&
                        (beforeSequenceOrder == null || j.SequenceOrder < beforeSequenceOrder) &&
                        ((j.SourceVolumeId != null && volumeIds.Contains(j.SourceVolumeId.Value)) ||
                         (j.TargetVolumeId != null && volumeIds.Contains(j.TargetVolumeId.Value))))
            .Select(j => new JobHead(
                j.Id, j.Type, j.SequenceOrder, j.SourceVolumeId, j.TargetVolumeId, j.TargetRelativePath))
            .ToListAsync(ct);

        if (heads.Count == 0) return null;

        var itemsByJob = await LoadRepresentativeItemsAsync(heads, ct);

        PendingConflict? worst = null;
        foreach (var head in heads.OrderBy(h => h.SequenceOrder))
        {
            var otherClaims = ClaimsOf(
                head.Type, head.SourceVolumeId, head.TargetVolumeId, head.TargetRelativePath,
                itemsByJob.TryGetValue(head.Id, out var items) ? items : []);

            if (otherClaims.Count == 0 || !Conflicts(claims, otherClaims)) continue;

            // Describe the blocking job by the path it is taking away when it has one (that is
            // the entity the user recognizes); otherwise by where it is going to land.
            var sourceIndex = otherClaims.FindIndex(c => c.IsSource);
            var path = otherClaims[sourceIndex < 0 ? 0 : sourceIndex].Path;
            worst = new PendingConflict(head.Id, head.SequenceOrder, head.Type, path);
        }

        return worst;
    }

    /// <summary>
    /// The items needed to describe the candidate jobs: the folder markers, plus — only for jobs
    /// that have no marker — their own items. A pending cross-volume MoveFolder therefore costs
    /// one row here instead of one per file.
    /// </summary>
    private async Task<Dictionary<int, List<OperationJobItem>>> LoadRepresentativeItemsAsync(
        List<JobHead> heads, CancellationToken ct)
    {
        var candidateIds = heads.Where(h => h.Type != JobType.CreateFolder).Select(h => h.Id).ToList();
        if (candidateIds.Count == 0) return [];

        var markers = await _db.OperationJobItems.AsNoTracking()
            .Where(i => candidateIds.Contains(i.JobId) && i.FileId == null)
            .ToListAsync(ct);

        var byJob = markers.GroupBy(i => i.JobId).ToDictionary(g => g.Key, g => g.ToList());

        var markerless = candidateIds.Where(id => !byJob.ContainsKey(id)).ToList();
        if (markerless.Count > 0)
        {
            var rest = await _db.OperationJobItems.AsNoTracking()
                .Where(i => markerless.Contains(i.JobId))
                .ToListAsync(ct);
            foreach (var group in rest.GroupBy(i => i.JobId))
                byJob[group.Key] = group.ToList();
        }

        return byJob;
    }

    private sealed record JobHead(
        int Id, JobType Type, int SequenceOrder, int? SourceVolumeId, int? TargetVolumeId,
        string? TargetRelativePath);
}
