using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Search;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// A re-scan used to truncate the volume's index and rebuild it: every <c>Files.Id</c> changed
/// (while <c>OperationJobItems.FileId</c> kept pointing at the old one) and every <c>Pending*</c>
/// field was wiped. This runs the real <see cref="ScanService"/> a second time over a volume that
/// has already been indexed and checks the three things that must survive it: the row identity,
/// the pending overlay, and the ability to run a real job against that same row afterwards.
///
/// Until step 9b writes the overlay at enqueue, the scenario stamps the <c>Pending*</c> fields by
/// hand — the point under test is the scan, not who wrote them.
///
/// Note for the operator: this is the one scenario that runs a full volume scan, so it is the
/// slowest in the suite. On a very large volume, raise <c>ScenarioTimeoutSeconds</c>.
/// </summary>
public sealed class RescanPreservesOverlayScenario : Scenario
{
    private const string KeepFile = @"rescan\rescanprobe-keep.dat";
    private const string VanishFile = @"rescan\rescanprobe-vanish.dat";
    private const string PendingRenameTo = "rescanprobe-renamed.dat";

    public override string Name => "rescan-preserves-overlay";

    public override string Description =>
        "A second full scan keeps row identities and the pending overlay, marks what vanished absent, and the job still runs.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        ctx.Source.CreateFile(KeepFile, 64 * 1024);
        var vanishFullPath = ctx.Source.CreateFile(VanishFile, 32 * 1024);
        await ctx.IndexSourceAsync(AllowEverything());

        var keepPath = ctx.Source.RelativePath(KeepFile);
        var vanishPath = ctx.Source.RelativePath(VanishFile);

        var keep = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, keepPath, "arrange");
        var vanish = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, vanishPath, "arrange");
        var dir = await AssertCatalogHasDirectoryAsync(
            ctx, ctx.SourceVolumeId, ScanPath.Parent(keepPath), "arrange");
        if (keep is null || vanish is null || dir is null) return;

        var keepId = keep.Id;
        var vanishId = vanish.Id;
        var dirId = dir.Id;

        await StampOverlayAsync(ctx, keepId);

        // The file leaves the disk between the two scans: the merge must mark the row absent,
        // not delete it.
        File.Delete(vanishFullPath);

        // ── act (a real, complete scan of the volume) ─────────────────────────
        await EnsureWatchedRootAsync(ctx);
        await ctx.Env.WithScopeAsync<object?>(async sp =>
        {
            await sp.GetRequiredService<ScanService>().ScanVolumeAsync(ctx.SourceVolumeId, ctx.Ct);
            return null;
        });
        ctx.Log("full scan of the source volume finished");

        // ── assert (identity, overlay, absence) ───────────────────────────────
        var keepAfter = await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId, keepPath, "after the re-scan");
        var dirAfter = await AssertCatalogHasDirectoryAsync(
            ctx, ctx.SourceVolumeId, ScanPath.Parent(keepPath), "after the re-scan");
        if (keepAfter is null || dirAfter is null) return;

        ctx.Assert.Equal(keepId, keepAfter.Id, "Files.Id of the re-found file (OperationJobItems point at it)");
        ctx.Assert.Equal(dirId, dirAfter.Id, "Directories.Id of the re-found folder");
        ctx.Assert.Equal(PendingRenameTo, keepAfter.PendingName ?? "(null)", "PendingName after the re-scan");
        ctx.Assert.Equal(
            EntityPendingState.PendingRename, keepAfter.PendingState, "PendingState after the re-scan");
        ctx.Assert.True(keepAfter.IsPresent, "the re-found file must stay present");

        var vanishAfter = await ctx.Env.WithDbAsync(db => db.Files.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == vanishId, ctx.Ct));
        if (vanishAfter is null)
            ctx.Assert.Fail("the file deleted from disk lost its catalog row: a scan must mark it absent, never delete it.");
        else
            ctx.Assert.True(!vanishAfter.IsPresent, "the file deleted from disk must be marked absent");

        // The search index followed the merge batch by batch.
        var hits = await SearchAsync(ctx, "rescanprobe-keep");
        ctx.Assert.True(hits.Contains(keepId),
            $"the re-found file must still be searchable; hits [{string.Join(", ", hits)}], expected id {keepId}");
        ctx.Assert.True(!hits.Contains(vanishId),
            $"the vanished file must not be a search hit any more; hits [{string.Join(", ", hits)}]");

        // ── assert (the row is still operable) ────────────────────────────────
        // The hand-written overlay is a marker, not a queue state, so the enqueue guard (which
        // reads OperationJobItems) lets this through — exactly as it would for a fresh row.
        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, keepId), ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"a job enqueued on a re-scanned row must complete; {Harness.QueueDriver.Describe(finished)}");
        ctx.Assert.True(File.Exists(ctx.Target.FullPath("rescanprobe-keep.dat")),
            "the moved file must exist in the target area");
        AssertNoPartialsAnywhere(ctx);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Writes the overlay step 9b will write at enqueue time.</summary>
    private static Task StampOverlayAsync(ScenarioContext ctx, int fileId) =>
        ctx.Env.WithDbAsync(async db =>
        {
            var row = await db.Files.FirstAsync(f => f.Id == fileId, ctx.Ct);
            row.PendingName = PendingRenameTo;
            row.PendingState = EntityPendingState.PendingRename;
            await db.SaveChangesAsync(ctx.Ct);
        });

    /// <summary>
    /// Scopes the scan to the scenario's own fixture area. Without an active watched root
    /// <see cref="ScanService"/> has nothing to scan, and with the volume root it would index the
    /// operator's whole drive into the throwaway harness database.
    /// </summary>
    private static Task EnsureWatchedRootAsync(ScenarioContext ctx) =>
        ctx.Env.WithDbAsync(async db =>
        {
            var root = ctx.Source.RootRelativePath;
            var exists = await db.WatchedRoots
                .AnyAsync(r => r.VolumeId == ctx.SourceVolumeId && r.RelativePath == root, ctx.Ct);
            if (exists) return;

            db.WatchedRoots.Add(new WatchedRoot
            {
                VolumeId = ctx.SourceVolumeId,
                RelativePath = root,
                IsActive = true,
            });
            await db.SaveChangesAsync(ctx.Ct);
        });

    private static Task<IReadOnlyList<int>> SearchAsync(ScenarioContext ctx, string text) =>
        ctx.Env.WithScopeAsync<IReadOnlyList<int>>(async sp =>
        {
            var result = await sp.GetRequiredService<IFileSearchIndex>().SearchAsync(
                new FileSearchQuery(
                    Text: text, Scope: SearchScope.Name, Category: null, Extensions: null,
                    SizeBytesMin: null, SizeBytesMax: null, ModifiedFrom: null, ModifiedTo: null,
                    VolumeId: ctx.SourceVolumeId, OnlineOnly: false, Sort: SearchSort.Relevance, Desc: false,
                    Skip: 0, Take: 50),
                ctx.Ct);
            return result.Items;
        });
}
