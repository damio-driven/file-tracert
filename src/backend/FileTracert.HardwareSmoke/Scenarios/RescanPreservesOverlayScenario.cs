using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// A re-scan used to truncate the volume's index and rebuild it: every <c>Files.Id</c> changed
/// (while <c>OperationJobItems.FileId</c> kept pointing at the old one) and every <c>Pending*</c>
/// field was wiped. This runs the real <see cref="ScanService"/> a second time over a volume that
/// has already been indexed and checks the three things that must survive it: the row identity,
/// the pending overlay, and the ability to run the very job that wrote that overlay afterwards.
///
/// The overlay comes from a real enqueue — since step 9b that is what writes it — so the scenario
/// covers the whole loop: queue an operation, re-scan under it, execute it, and find both the
/// physical fact applied and the overlay gone.
///
/// Note for the operator: this is the one scenario that runs a full volume scan, so it is the
/// slowest in the suite. On a very large volume, raise <c>ScenarioTimeoutSeconds</c>.
/// </summary>
public sealed class RescanPreservesOverlayScenario : Scenario
{
    private const string KeepFile = @"rescan\rescanprobe-keep.dat";
    private const string VanishFile = @"rescan\rescanprobe-vanish.dat";
    private const string PendingRenameTo = "rescanprobe-renamed.dat";

    /// <summary>
    /// Filler files, so the two scan timings say something about a real workload instead of
    /// timing two rows. Small on purpose: the merge cost is per row, not per byte.
    /// </summary>
    private const int FillerFiles = 2_000;

    private const int FillerFileBytes = 1024;

    public override string Name => "rescan-preserves-overlay";

    public override string Description =>
        "A second full scan keeps row identities and the pending overlay, marks what vanished absent, and the job still runs.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange (through the real scan, so both scan costs are measured) ──
        ctx.Source.CreateFile(KeepFile, 64 * 1024);
        var vanishFullPath = ctx.Source.CreateFile(VanishFile, 32 * 1024);
        for (var i = 0; i < FillerFiles; i++)
        {
            ctx.Source.CreateFile($@"rescan\filler\f{i:D5}.dat", FillerFileBytes);
        }

        await EnsureWatchedRootAsync(ctx, ctx.Source, ctx.SourceVolumeId);
        var firstScan = await ScanVolumeAsync(ctx, ctx.SourceVolumeId);
        ctx.Log($"first scan of {FillerFiles + 2} files (empty catalog, bulk-insert path): " +
                $"{firstScan.TotalSeconds:0.00}s");

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

        // The overlay is written by the real enqueue (step 9b) — no hand-stamped Pending* fields.
        var job = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = keepId,
            NewName = PendingRenameTo,
        }, ctx.Ct);

        // The file leaves the disk between the two scans: the merge must mark the row absent,
        // not delete it.
        File.Delete(vanishFullPath);

        // ── act (a second complete scan of the same volume) ───────────────────
        var reScan = await ScanVolumeAsync(ctx, ctx.SourceVolumeId);

        // Before 9a a re-scan was a truncate + full bulk insert, i.e. it cost what the first
        // scan costs here — so these two numbers are the before/after of the same operation.
        ctx.Log($"re-scan (merge path): {reScan.TotalSeconds:0.00}s " +
                $"— before 9a a re-scan cost a full rebuild, i.e. the first-scan number above.");

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
        ctx.Assert.Equal(job.Id, keepAfter.PendingJobId ?? -1, "PendingJobId after the re-scan");
        ctx.Assert.True(keepAfter.IsPresent, "the re-found file must stay present");

        // The search index followed the merge batch by batch — and it carries the PROJECTED name,
        // so the queued rename is what answers.
        var hits = await SearchByNameAsync(ctx, "rescanprobe-renamed");
        ctx.Assert.True(hits.Contains(keepId),
            $"the re-scanned file must still be searchable under its projected name; hits [{string.Join(", ", hits)}], expected id {keepId}");
        var vanishHits = await SearchByNameAsync(ctx, "rescanprobe-vanish");
        ctx.Assert.True(!vanishHits.Contains(vanishId),
            $"the vanished file must not be a search hit any more; hits [{string.Join(", ", vanishHits)}]");

        var vanishAfter = await ctx.Env.WithDbAsync(db => db.Files.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == vanishId, ctx.Ct));
        if (vanishAfter is null)
            ctx.Assert.Fail("the file deleted from disk lost its catalog row: a scan must mark it absent, never delete it.");
        else
            ctx.Assert.True(!vanishAfter.IsPresent, "the file deleted from disk must be marked absent");

        // ── assert (the job that wrote the overlay still runs) ────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"a job enqueued before a re-scan must still complete after it; {Harness.QueueDriver.Describe(finished)}");
        ctx.Assert.FileExists(
            ctx.Source.FullPath($@"rescan\{PendingRenameTo}"), "the renamed file on disk");

        var keepDone = await ctx.Env.WithDbAsync(db => db.Files.AsNoTracking()
            .FirstAsync(f => f.Id == keepId, ctx.Ct));
        ctx.Assert.Equal(PendingRenameTo, keepDone.Name, "the physical name after the rename ran");
        ctx.Assert.Equal(EntityPendingState.None, keepDone.PendingState, "the overlay after completion");
        AssertNoPartialsAnywhere(ctx);
    }
}
