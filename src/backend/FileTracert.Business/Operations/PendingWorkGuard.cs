using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;
using FileTracert.Data.Entities;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Operations;

/// <summary>What a job intends to do with a path it lays claim to.</summary>
public enum ClaimKind
{
    /// <summary>Take it AWAY: rename it, move it out of there. Whatever sits at, above or below it loses its ground.</summary>
    Source,

    /// <summary>Put something THERE. Two targets on the same path collide; two that merely nest do not (§5).</summary>
    Target,

    /// <summary>
    /// READ it and leave it. Step 15a: the source of a Copy. It is deliberately neither of the
    /// other two — calling it a Source would make two copies of one file serialize behind each
    /// other for no reason, and calling it a Target would make them collide on a destination
    /// neither of them is writing to.
    /// </summary>
    Read
}

/// <summary>A volume-scoped path a job lays claim to. The same relative path on another volume is another place.</summary>
/// <param name="VolumeId">Volume the path belongs to.</param>
/// <param name="Path">Volume-relative path (normalized, no leading/trailing separator).</param>
/// <param name="Kind">What the job intends to do with it — see <see cref="ClaimKind"/>.</param>
public readonly record struct PathClaim(int VolumeId, string Path, ClaimKind Kind);

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
///     path pulls the ground from under anything at, above or below it;</item>
///   <item>a TARGET path of one overlaps a READ path of the other — somebody is landing bytes
///     where somebody else is reading them; or</item>
///   <item>two TARGET paths are EQUAL — two operations landing on the very same destination.</item>
/// </list>
/// Two targets that merely nest do NOT conflict: that is exactly §5's «I queue folder X and then
/// move files into it», which must stay legal — the second job resolves X in the projection.
/// Two READS never conflict at all.
///
/// <para><b>Step 15a — the source of a Copy is a READ, not a SOURCE.</b> The rule above was
/// written when every queueable operation TOOK its source away, so "claims this path" and "is
/// about to remove this path" were the same statement. A copy leaves the original exactly where
/// it is. Filed as a Source it would serialize two copies of one file into two different folders
/// behind each other for no reason; filed as a Target it would be worse, because
/// <c>SameTarget</c> compares paths for equality and those two copies share a source path — they
/// would be read as landing on the same destination, which is the opposite of true. Hence the
/// third kind, and the third clause of the rule: a read still loses to anything that removes or
/// overwrites what it is reading, and to nothing else.</para>
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
            Add(claims, targetVolumeId, targetRelativePath, ClaimKind.Target);
            return claims;
        }

        // The one line that step 15a turns on: a Copy reads its source, everything else takes it.
        var sourceKind = type is JobType.CopyFile or JobType.CopyFolder
            ? ClaimKind.Read
            : ClaimKind.Source;

        // Folder ops carry a FileId-less marker item that IS the folder; when one exists it
        // subsumes the expanded file items. Jobs written before the marker existed have none —
        // fall back to every item, which is a superset and therefore still safe.
        var markers = items.Where(i => i.FileId is null).ToList();
        var representative = markers.Count > 0 ? markers : items;

        foreach (var item in representative)
        {
            Add(claims, sourceVolumeId, item.SourceRelativePath, sourceKind);
            Add(claims, targetVolumeId, item.TargetRelativePath, ClaimKind.Target);
        }

        return claims;
    }

    private static void Add(List<PathClaim> claims, int? volumeId, string? path, ClaimKind kind)
    {
        if (volumeId is null || string.IsNullOrEmpty(path)) return;
        claims.Add(new PathClaim(volumeId.Value, path, kind));
    }

    /// <summary>
    /// The conflict rule, stated once. Symmetric by construction: it is asked in both directions
    /// so «my source under your folder» and «your folder over my source» are the same answer.
    /// </summary>
    public static bool Conflicts(IReadOnlyCollection<PathClaim> a, IReadOnlyCollection<PathClaim> b) =>
        SourceOverlapsAnything(a, b) || SourceOverlapsAnything(b, a) ||
        TargetOverlapsRead(a, b) || TargetOverlapsRead(b, a) ||
        SameTarget(a, b);

    /// <summary>A path being taken away pulls the ground from under anything at, above or below it.</summary>
    private static bool SourceOverlapsAnything(
        IReadOnlyCollection<PathClaim> sources, IReadOnlyCollection<PathClaim> others) =>
        sources.Where(s => s.Kind == ClaimKind.Source).Any(s => others.Any(o =>
            o.VolumeId == s.VolumeId && ScanPath.Overlaps(s.Path, o.Path)));

    /// <summary>
    /// Somebody lands bytes where somebody else is reading them. Overlap, not equality: a folder
    /// arriving at <c>Backup</c> changes what a copy reading <c>Backup\x.txt</c> would find, and a
    /// file arriving inside a folder a copy is walking is content that copy never planned for.
    /// Both are cases where the reader's snapshot of its own source stops describing the disk.
    /// </summary>
    private static bool TargetOverlapsRead(
        IReadOnlyCollection<PathClaim> writers, IReadOnlyCollection<PathClaim> readers) =>
        writers.Where(w => w.Kind == ClaimKind.Target).Any(w => readers.Any(r =>
            r.Kind == ClaimKind.Read && r.VolumeId == w.VolumeId && ScanPath.Overlaps(w.Path, r.Path)));

    /// <summary>Two operations landing on the very same destination. Nesting is legal (§5).</summary>
    private static bool SameTarget(IReadOnlyCollection<PathClaim> a, IReadOnlyCollection<PathClaim> b) =>
        a.Where(x => x.Kind == ClaimKind.Target).Any(x => b.Any(y =>
            y.Kind == ClaimKind.Target && y.VolumeId == x.VolumeId && ScanPath.SamePath(x.Path, y.Path)));

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
            // the entity the user recognizes), by the one it is reading when it is a copy, and
            // otherwise by where it is going to land.
            var sourceIndex = otherClaims.FindIndex(c => c.Kind != ClaimKind.Target);
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
