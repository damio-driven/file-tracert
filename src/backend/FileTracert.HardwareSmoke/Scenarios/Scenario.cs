using FileTracert.Business.Filtering;
using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Search;
using FileTracert.Data.Entities;
using FileTracert.HardwareSmoke.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// Optional service overrides applied on top of the product registrations when this
    /// scenario's environment is built. Fault-injection scenarios use it to wrap ONE real
    /// service (e.g. a first-call-fails <c>IFileSearchIndex</c>) while everything else stays real.
    /// </summary>
    public virtual Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? ConfigureServices => null;

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
                ScanPath.Join(f.Directory.MaterializedPath, f.Name),
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

    // Since fix #7 the index update commits INSIDE the Completed transaction, so the moment a
    // job is observed terminal the catalog is fully written — assertions read once, no settle
    // polling. (The old 15 s wait existed only to paper over the pre-fix race.)

    /// <summary>
    /// Asserts a file row exists at the expected volume-relative path, and on failure says what the
    /// catalog actually holds — a bare "row not found" costs a debugging round-trip.
    /// Returns the row so the caller can assert more on it.
    /// </summary>
    protected static async Task<FileEntry?> AssertCatalogHasFileAsync(
        ScenarioContext ctx, int volumeId, string volumeRelativePath, string what)
    {
        var row = await FindFileRowAsync(ctx, volumeId, volumeRelativePath);
        if (row is null)
            ctx.Assert.Fail($"{what}: no Files row at '{volumeRelativePath}' on volume {volumeId}. " +
                            $"{await DescribeCatalogAsync(ctx)}");
        return row;
    }

    /// <summary>Directory counterpart of <see cref="AssertCatalogHasFileAsync"/>.</summary>
    protected static async Task<DirectoryNode?> AssertCatalogHasDirectoryAsync(
        ScenarioContext ctx, int volumeId, string materializedPath, string what)
    {
        var row = await FindDirectoryRowAsync(ctx, volumeId, materializedPath);
        if (row is null)
            ctx.Assert.Fail($"{what}: no Directories row at '{materializedPath}' on volume {volumeId}. " +
                            $"{await DescribeCatalogAsync(ctx)}");
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
    /// Rewrites a volume's last-known free bytes — the estimate the catalog carries between two
    /// volume probes. Since step 11b this is NOT how a volume is put under space pressure: the
    /// feasibility checks read the drive itself, and this column only survives as the fallback
    /// for a volume that cannot answer. Scenarios use it the other way round now, to plant a
    /// stale figure and prove the check ignores it.
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

    /// <summary>
    /// Free bytes on a test volume as the DEVICE reports them right now, through the same port
    /// the queue uses. The scenarios size their demand against this instead of filling the drive:
    /// a fixture that has to occupy hundreds of gigabytes to be meaningful is not a test, it is
    /// an outage, and the check under scrutiny compares demand with free space — it cannot tell
    /// which of the two moved.
    /// </summary>
    protected static long LiveFreeBytes(ScenarioContext ctx, TestVolume volume) =>
        ctx.Env.Services.GetRequiredService<IVolumeProbe>().TryGetFreeBytes(volume.VolumeGuid)
        ?? throw new InvalidOperationException(
            $"Test volume '{volume.Name}' ({volume.VolumeGuid}) did not answer the free-space probe.");

    /// <summary>
    /// Rewrites the indexed size of a catalog row, so the operation queued from it demands more
    /// room than the target really has. The demand is what the product computes from this column,
    /// so nothing downstream is faked.
    /// </summary>
    protected static Task SetIndexedSizeAsync(ScenarioContext ctx, int fileId, long sizeBytes) =>
        ctx.Env.WithDbAsync(async db =>
        {
            var file = await db.Files.FirstAsync(f => f.Id == fileId, ctx.Ct);
            file.SizeBytes = sizeBytes;
            await db.SaveChangesAsync(ctx.Ct);
        });

    /// <summary>
    /// Rewrites an already-queued job's demand on the target. This is the harness's stand-in for
    /// "another process filled the drive after the enqueue": the execution re-check compares a
    /// demand with the live free space, and moving either side of that comparison exercises the
    /// same branch — while filling a real 300 GB drive for a few seconds would not.
    /// </summary>
    protected static Task SetJobRequiredBytesAsync(ScenarioContext ctx, int jobId, long requiredBytes) =>
        ctx.Env.WithDbAsync(async db =>
        {
            var job = await db.OperationJobs.FirstAsync(j => j.Id == jobId, ctx.Ct);
            job.RequiredBytesTarget = requiredBytes;
            await db.SaveChangesAsync(ctx.Ct);
        });

    /// <summary>Sets the §4 safety margin the hard check adds on top of the demand.</summary>
    protected static Task SetSpaceMarginPercentAsync(ScenarioContext ctx, int percent) =>
        ctx.Env.WithDbAsync(async db =>
        {
            var settings = await db.AppSettings.FirstOrDefaultAsync(ctx.Ct);
            if (settings is null)
            {
                db.AppSettings.Add(new AppSettings
                {
                    ApiToken = "harness", SpaceMarginPercent = percent,
                    DefaultExtensionFilter = [], ExcludedPaths = [],
                });
            }
            else
            {
                settings.SpaceMarginPercent = percent;
            }
            await db.SaveChangesAsync(ctx.Ct);
        });

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

    /// <summary>
    /// Runs one real full scan of a volume through the product's own <see cref="ScanService"/>,
    /// and reports how long it took. The slowest thing a scenario can do (an NTFS scan walks the
    /// MFT), so only the scenarios that are ABOUT the scan should call it.
    /// </summary>
    protected static async Task<TimeSpan> ScanVolumeAsync(ScenarioContext ctx, int volumeId)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        await ctx.Env.WithScopeAsync<object?>(async sp =>
        {
            await sp.GetRequiredService<ScanService>().ScanVolumeAsync(volumeId, ctx.Ct);
            return null;
        });
        started.Stop();
        return started.Elapsed;
    }

    /// <summary>
    /// Scopes a scan to one fixture area. Without an active watched root
    /// <see cref="ScanService"/> has nothing to scan, and with the volume root it would index the
    /// operator's whole drive into the throwaway harness database.
    /// </summary>
    protected static Task EnsureWatchedRootAsync(ScenarioContext ctx, FixtureArea area, int volumeId) =>
        ctx.Env.WithDbAsync(async db =>
        {
            var root = area.RootRelativePath;
            var exists = await db.WatchedRoots
                .AnyAsync(r => r.VolumeId == volumeId && r.RelativePath == root, ctx.Ct);
            if (exists) return;

            db.WatchedRoots.Add(new WatchedRoot
            {
                VolumeId = volumeId,
                RelativePath = root,
                IsActive = true,
            });
            await db.SaveChangesAsync(ctx.Ct);
        });

    /// <summary>Runs a name-scoped search through the real FTS index and returns the file ids.</summary>
    protected static Task<IReadOnlyList<int>> SearchByNameAsync(ScenarioContext ctx, string text) =>
        ctx.Env.WithScopeAsync<IReadOnlyList<int>>(async sp =>
        {
            var result = await sp.GetRequiredService<IFileSearchIndex>().SearchAsync(
                new FileSearchQuery(
                    Text: text, Scope: SearchScope.Name, Category: null, Extensions: null,
                    SizeBytesMin: null, SizeBytesMax: null, ModifiedFrom: null, ModifiedTo: null,
                    VolumeId: null, OnlineOnly: false, Sort: SearchSort.Relevance, Desc: false,
                    Skip: 0, Take: 50),
                ctx.Ct);
            return result.Items;
        });

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
