using FileTracert.Business.Filtering;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data.Entities;
using FileTracert.HardwareSmoke.Harness;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>Which source→target combination a scenario is meaningful on.</summary>
public enum PairRequirement
{
    /// <summary>Source and target on the same physical volume (metadata-only path).</summary>
    Intra,

    /// <summary>Source and target on different physical volumes (copy→verify→finalize path).</summary>
    Cross,

    Any,
}

/// <summary>
/// A named arrange → act → assert case. <see cref="RunAsync"/> arranges fixtures on real disks,
/// acts through the real queue services, and records assertion failures on
/// <see cref="ScenarioContext.Assert"/>. Throwing <see cref="ScenarioSkippedException"/> reports
/// SKIPPED; any other exception is a FAIL with the full exception attached.
/// </summary>
public abstract class Scenario
{
    public abstract string Name { get; }

    /// <summary>One line, printed above the scenario's log output.</summary>
    public abstract string Description { get; }

    public abstract PairRequirement Requires { get; }

    /// <summary>Interactive scenarios only run when the operator opted in and is present.</summary>
    public virtual bool NeedsSemiAutomatic => false;

    /// <summary>Scenarios that ask for a drive to be unplugged need the target to be removable.</summary>
    public virtual bool NeedsExternalTarget => false;

    public bool AppliesTo(VolumePair pair, HardwareSmokeOptions options)
    {
        if (Requires == PairRequirement.Cross && !pair.IsCrossVolume) return false;
        if (Requires == PairRequirement.Intra && pair.IsCrossVolume) return false;
        if (NeedsSemiAutomatic && !options.SemiAutomatic) return false;
        if (NeedsExternalTarget && pair.Target.Kind != TestVolumeKind.External) return false;
        return true;
    }

    public abstract Task RunAsync(ScenarioContext ctx);

    // ── shared arrange/assert helpers (kept here so no scenario re-implements them) ──

    /// <summary>Allow only the given extensions — the shape of a real "images only" watched root.</summary>
    protected static EffectiveFilter AllowOnly(params string[] extensions) =>
        new(new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase), []);

    /// <summary>No extension restriction: everything the scanner sees is indexed.</summary>
    protected static EffectiveFilter AllowEverything() => new(new HashSet<string>(), []);

    /// <summary>
    /// The standard post-conditions of a finished cross-volume move: nothing half-written is left
    /// on either side. Asserted by every scenario that touches the copy path, whatever its outcome.
    /// </summary>
    protected static void AssertNoPartialsAnywhere(ScenarioContext ctx)
    {
        ctx.Assert.NoPartialsUnder(ctx.Target.RootFullPath, "target area after the job");
        ctx.Assert.NoPartialsUnder(ctx.Source.RootFullPath, "source area after the job");
    }

    /// <summary>Loads the catalog row for a file by its volume-relative path, or null.</summary>
    protected static async Task<FileEntry?> FindFileRowAsync(
        ScenarioContext ctx, int volumeId, string volumeRelativePath)
    {
        return await ctx.Env.WithDbAsync(async db =>
        {
            var rows = await db.Files
                .Include(f => f.Directory)
                .AsNoTracking()
                .Where(f => f.VolumeId == volumeId)
                .ToListAsync(ctx.Ct);

            return rows.FirstOrDefault(f => string.Equals(
                Business.Scanning.ScanPath.Join(f.Directory.MaterializedPath, f.Name),
                volumeRelativePath,
                StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// Loads the catalog row for a directory by its materialized path, or null. The comparison is
    /// case-insensitive in memory on purpose: SQLite's default BINARY collation would make an
    /// assert fail on a casing difference that Windows itself does not consider a difference, and
    /// the harness must report real defects, not collation trivia.
    /// </summary>
    protected static async Task<DirectoryNode?> FindDirectoryRowAsync(
        ScenarioContext ctx, int volumeId, string materializedPath)
    {
        return await ctx.Env.WithDbAsync(async db =>
        {
            var rows = await db.Directories
                .AsNoTracking()
                .Where(d => d.VolumeId == volumeId)
                .ToListAsync(ctx.Ct);

            return rows.FirstOrDefault(d =>
                string.Equals(d.MaterializedPath, materializedPath, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// How long a catalog assertion keeps re-reading before giving up. The engine commits
    /// <c>Completed</c> BEFORE it runs the index update, so the instant a job looks terminal the
    /// catalog may still be half-written — asserting on that snapshot would flake. Filesystem
    /// assertions need no such wait: the source is recycled before the Completed transition.
    /// </summary>
    private static readonly TimeSpan CatalogSettleTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Re-reads <paramref name="probe"/> until it yields a row or the settle window expires.</summary>
    private static async Task<T?> WaitForCatalogAsync<T>(ScenarioContext ctx, Func<Task<T?>> probe)
        where T : class
    {
        var deadline = DateTime.UtcNow + CatalogSettleTimeout;
        while (true)
        {
            var result = await probe();
            if (result is not null) return result;
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(50, ctx.Ct);
        }
    }

    /// <summary>Re-evaluates a boolean post-condition until it holds or the settle window expires.</summary>
    protected static async Task<bool> WaitForCatalogConditionAsync(ScenarioContext ctx, Func<Task<bool>> probe)
    {
        var deadline = DateTime.UtcNow + CatalogSettleTimeout;
        while (true)
        {
            if (await probe()) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(50, ctx.Ct);
        }
    }

    /// <summary>
    /// Asserts a file row exists at the expected volume-relative path, and on failure says what the
    /// catalog actually holds — a bare "row not found" costs a debugging round-trip.
    /// Returns the row so the caller can assert more on it.
    /// </summary>
    protected static async Task<FileEntry?> AssertCatalogHasFileAsync(
        ScenarioContext ctx, int volumeId, string volumeRelativePath, string what)
    {
        var row = await WaitForCatalogAsync(ctx, () => FindFileRowAsync(ctx, volumeId, volumeRelativePath));
        if (row is null)
            ctx.Assert.Fail($"{what}: no Files row at '{volumeRelativePath}' on volume {volumeId} " +
                            $"after {CatalogSettleTimeout.TotalSeconds:0}s. {await DescribeCatalogAsync(ctx)}");
        return row;
    }

    /// <summary>Directory counterpart of <see cref="AssertCatalogHasFileAsync"/>.</summary>
    protected static async Task<DirectoryNode?> AssertCatalogHasDirectoryAsync(
        ScenarioContext ctx, int volumeId, string materializedPath, string what)
    {
        var row = await WaitForCatalogAsync(ctx, () => FindDirectoryRowAsync(ctx, volumeId, materializedPath));
        if (row is null)
            ctx.Assert.Fail($"{what}: no Directories row at '{materializedPath}' on volume {volumeId} " +
                            $"after {CatalogSettleTimeout.TotalSeconds:0}s. {await DescribeCatalogAsync(ctx)}");
        return row;
    }

    /// <summary>Every file and directory row in the scenario's throwaway catalog, for failure messages.</summary>
    protected static Task<string> DescribeCatalogAsync(ScenarioContext ctx) =>
        ctx.Env.WithDbAsync(async db =>
        {
            var files = await db.Files.Include(f => f.Directory).AsNoTracking()
                .Select(f => $"v{f.VolumeId}:{f.Directory.MaterializedPath}|{f.Name}")
                .ToListAsync(ctx.Ct);
            var dirs = await db.Directories.AsNoTracking()
                .Select(d => $"v{d.VolumeId}:{d.MaterializedPath}")
                .ToListAsync(ctx.Ct);

            return $"Catalog now holds files [{string.Join(", ", files)}] and directories [{string.Join(", ", dirs)}].";
        });

    /// <summary>
    /// Rewrites a volume's last-known free bytes. The queue's feasibility is computed against this
    /// persisted estimate (exactly as it is in production between two volume probes), so this is
    /// how the harness puts a volume under space pressure without having to physically fill a disk.
    /// </summary>
    protected static async Task SetVolumeFreeBytesAsync(ScenarioContext ctx, int volumeId, long freeBytes)
    {
        await ctx.Env.WithDbAsync(async db =>
        {
            var volume = await db.Volumes.FirstAsync(v => v.Id == volumeId, ctx.Ct);
            volume.FreeBytesLastKnown = freeBytes;
            await db.SaveChangesAsync(ctx.Ct);
        });
    }

    /// <summary>Flips a volume's online flag, the way the volume sync does when a drive disappears.</summary>
    protected static async Task SetVolumeOnlineAsync(ScenarioContext ctx, int volumeId, bool isOnline)
    {
        await ctx.Env.WithDbAsync(async db =>
        {
            var volume = await db.Volumes.FirstAsync(v => v.Id == volumeId, ctx.Ct);
            volume.IsOnline = isOnline;
            await db.SaveChangesAsync(ctx.Ct);
        });
    }

    /// <summary>Builds a move-file request against the target area's root.</summary>
    protected static CreateJobRequest MoveFileTo(ScenarioContext ctx, int fileId, string targetSubfolder = "") =>
        new()
        {
            Type = JobType.MoveFile,
            SourceFileId = fileId,
            TargetVolumeId = ctx.TargetVolumeId,
            TargetRelativePath = ctx.Target.RelativePath(targetSubfolder),
        };

    /// <summary>Builds a move-folder request landing the folder under the target area's root.</summary>
    protected static CreateJobRequest MoveFolderTo(ScenarioContext ctx, int directoryId, string targetSubfolder = "") =>
        new()
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = directoryId,
            TargetVolumeId = ctx.TargetVolumeId,
            TargetRelativePath = ctx.Target.RelativePath(targetSubfolder),
        };
}
