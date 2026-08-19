using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Projection;

/// <summary>Where a directory sits in the projection: its path, and the volume it lands on.</summary>
/// <param name="Path">Volume-relative path with every overlay on the way up applied.</param>
/// <param name="VolumeId">
/// The volume of the projected chain's root. A cross-volume move re-parents a row under a
/// directory that lives on ANOTHER volume, so the projected volume of an entity is the volume of
/// its projected directory, not its own <c>VolumeId</c> — that only changes at execution (§5).
/// </param>
public sealed record ProjectedLocation(string Path, int VolumeId);

/// <summary>
/// Resolves the PROJECTED path of directories: the physical <c>MaterializedPath</c> is only true
/// while nothing is queued, and §5 wants the Catalog and the Search results to show where things
/// are going, not where they still are.
///
/// Lives in Business, not in a controller: Host may depend on Business, never the other way round
/// (§3), and the Search results, the Catalog and any later caller must all get the same answer.
/// </summary>
public sealed class ProjectedPathResolver
{
    /// <summary>
    /// Hard stop on the upward walk. A <c>PendingParentId</c> that points inside its own subtree
    /// would otherwise loop forever. The enqueue rejects the intra-volume case (C22) but not the
    /// cross-volume one, and a hand-edited database answers to nobody — so the walk defends
    /// itself, loudly (§9: never a mute catch).
    /// </summary>
    private const int MaxDepth = 256;

    private readonly FileTracertDbContext _db;
    private readonly ILogger<ProjectedPathResolver> _logger;

    public ProjectedPathResolver(FileTracertDbContext db, ILogger<ProjectedPathResolver> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Projected location of every requested directory, in batch — the callers page their results,
    /// so this runs on tens of rows, and the ancestor closure is loaded one generation per query
    /// instead of one query per row.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, ProjectedLocation>> ResolveDirectoriesAsync(
        IReadOnlyCollection<int> directoryIds, CancellationToken ct)
    {
        var result = new Dictionary<int, ProjectedLocation>();
        if (directoryIds.Count == 0) return result;

        var wanted = directoryIds.Distinct().ToList();
        var nodes = await LoadAsync(wanted, ct);

        // Fast path — and the normal one: with an empty queue nothing overrides anything, so the
        // denormalized path IS the projected path and no ancestor walk is needed at all.
        if (!await _db.Directories.AnyAsync(d => d.PendingState != EntityPendingState.None, ct))
        {
            foreach (var id in wanted)
            {
                if (nodes.TryGetValue(id, out var node))
                    result[id] = new ProjectedLocation(node.MaterializedPath, node.VolumeId);
            }
            return result;
        }

        await LoadAncestorClosureAsync(nodes, ct);

        var memo = new Dictionary<int, ProjectedLocation>();
        foreach (var id in wanted)
        {
            if (!nodes.ContainsKey(id)) continue;
            result[id] = Locate(id, nodes, memo);
        }
        return result;
    }

    // ── the walk ──────────────────────────────────────────────────────────────

    private ProjectedLocation Locate(int id, Dictionary<int, Node> nodes, Dictionary<int, ProjectedLocation> memo)
    {
        if (memo.TryGetValue(id, out var known)) return known;

        // Collect the chain upwards first, then unwind it: iterative rather than recursive so the
        // cycle guard is a plain visited-set instead of a stack overflow.
        var chain = new List<Node>();
        var visited = new HashSet<int>();
        ProjectedLocation? anchor = null;
        int? current = id;

        while (current is not null)
        {
            if (memo.TryGetValue(current.Value, out var cached))
            {
                anchor = cached;
                break;
            }

            if (!visited.Add(current.Value))
            {
                anchor = Bail(nodes, chain, id,
                    $"a pending parent points back into its own subtree at directory {current.Value}");
                break;
            }

            if (chain.Count >= MaxDepth)
            {
                anchor = Bail(nodes, chain, id, $"the projected chain is deeper than {MaxDepth} levels");
                break;
            }

            if (!nodes.TryGetValue(current.Value, out var node))
            {
                anchor = Bail(nodes, chain, id, $"directory {current.Value} is missing from the catalog");
                break;
            }

            chain.Add(node);
            current = node.ProjectedParentId;
        }

        // Ran out of parents: the deepest node in the chain sits at the volume root. A row whose
        // parent is null but whose name is not empty is a root-level folder (the intra-volume
        // folder move detaches those), and Join("", name) yields exactly its own name.
        anchor ??= new ProjectedLocation(string.Empty, chain[^1].VolumeId);

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            // The chain's own root contributed the anchor above, so skip re-appending its name
            // only when the anchor came from "no parent" — Join handles the empty name anyway.
            anchor = new ProjectedLocation(ScanPath.Join(anchor.Path, node.ProjectedName), anchor.VolumeId);
            memo[node.Id] = anchor;
        }

        return memo[id];
    }

    /// <summary>
    /// Abandons the walk and falls back to the physical position of the deepest node reached.
    /// Never silent (§9): a broken parent chain is a real defect somewhere upstream and the log
    /// has to name the directory it happened on.
    /// </summary>
    private ProjectedLocation Bail(
        Dictionary<int, Node> nodes, List<Node> chain, int startId, string reason)
    {
        _logger.LogWarning(
            "Projected path of directory {Id} abandoned: {Reason}. Falling back to the physical path.",
            startId, reason);

        var deepest = chain.Count > 0 ? chain[^1] : null;
        if (deepest is null && nodes.TryGetValue(startId, out var start)) deepest = start;

        // The fallback stands in for the deepest node's PARENT, and the unwind then appends that
        // node's own name — so hand back the parent path, not the node's full path.
        return deepest is null
            ? new ProjectedLocation(string.Empty, 0)
            : new ProjectedLocation(ScanPath.Parent(deepest.MaterializedPath), deepest.VolumeId);
    }

    // ── loading ───────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, Node>> LoadAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var rows = await _db.Directories.AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .Select(d => new Node(
                d.Id, d.VolumeId, d.ParentId, d.PendingParentId, d.Name, d.PendingName, d.MaterializedPath))
            .ToListAsync(ct);
        return rows.ToDictionary(n => n.Id);
    }

    /// <summary>
    /// Pulls in the ancestors of everything already loaded, one generation per query. Bounded by
    /// <see cref="MaxDepth"/> so a cyclic <c>PendingParentId</c> cannot turn this into a loop.
    /// </summary>
    private async Task LoadAncestorClosureAsync(Dictionary<int, Node> nodes, CancellationToken ct)
    {
        for (int generation = 0; generation < MaxDepth; generation++)
        {
            var missing = nodes.Values
                .Select(n => n.ProjectedParentId)
                .Where(id => id.HasValue && !nodes.ContainsKey(id!.Value))
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            if (missing.Count == 0) return;

            foreach (var (id, node) in await LoadAsync(missing, ct))
                nodes[id] = node;
        }

        _logger.LogWarning(
            "Projected path resolution stopped loading ancestors after {Max} generations — " +
            "the directory tree looks cyclic.", MaxDepth);
    }

    private sealed record Node(
        int Id, int VolumeId, int? ParentId, int? PendingParentId,
        string Name, string? PendingName, string MaterializedPath)
    {
        public int? ProjectedParentId => PendingParentId ?? ParentId;

        public string ProjectedName => string.IsNullOrEmpty(PendingName) ? Name : PendingName;
    }
}
