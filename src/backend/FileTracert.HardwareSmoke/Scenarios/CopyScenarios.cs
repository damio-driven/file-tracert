using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Step 15a — a copy that stays on ONE volume, which is the case the old model could not express
/// at all. Every other intra-volume operation is metadata (§5: instant, O(1), no reservation); a
/// copy within a volume writes a second set of bytes onto the volume it reads from, so it takes a
/// ledger reservation, travels the whole state machine, and leaves TWO files on disk.
/// </summary>
public sealed class CopyIntraVolumeScenario : Scenario
{
    public override string Name => "copy-intra-volume";

    public override string Description =>
        "Copy inside one volume: both files on disk, both rows in the catalog, reservation released.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"origin\report.bin";
        var sourceAbsolute = ctx.Source.CreateFile(relative, 32 * 1024);
        var sourceHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        var destinationRelative = ctx.Source.RelativePath("duplicate");
        var destinationAbsolute = Path.Combine(ctx.Source.RootFullPath, "duplicate", "report.bin");

        // ── act ───────────────────────────────────────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = fileRow.Id,
            // Same volume on both ends — the point of the scenario.
            TargetVolumeId = ctx.SourceVolumeId,
            TargetRelativePath = destinationRelative,
        }, ctx.Ct);

        // Asserted BEFORE the worker runs: a reservation an intra-volume job could not have taken
        // before this step, and it must exist while the job is still queued, not just in theory.
        ctx.Assert.True(job.RequiredBytesTarget > 0,
            $"an intra-volume copy must demand room on its own volume (asked {job.RequiredBytesTarget} B)");
        ctx.Assert.Equal(0L, job.FreedBytesSource,
            "a copy frees nothing — the original stays where it is");

        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state ({Harness.QueueDriver.Describe(finished)})");

        // Two files, same bytes. The assertion a move could never pass.
        ctx.Assert.FileExists(sourceAbsolute, "the ORIGINAL after a copy");
        if (File.Exists(sourceAbsolute))
            ctx.Assert.Equal(sourceHash, ScenarioAssertions.Sha256(sourceAbsolute), "original content after a copy");

        ctx.Assert.FileExists(destinationAbsolute, "the COPY on disk");
        if (File.Exists(destinationAbsolute))
            ctx.Assert.Equal(sourceHash, ScenarioAssertions.Sha256(destinationAbsolute), "copied content");

        AssertNoPartialsAnywhere(ctx);

        // Both rows in the catalog, and the copy is a row of its own — not the source moved.
        var originalRow = await AssertCatalogHasFileAsync(
            ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative), "the original after a copy");
        var copyRow = await AssertCatalogHasFileAsync(
            ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(@"duplicate\report.bin"), "the copy in the catalog");

        if (originalRow is not null && copyRow is not null)
        {
            ctx.Assert.True(originalRow.Id != copyRow.Id,
                "a copy is a NEW entity, so it must not be the source row re-pointed");
            ctx.Assert.True(copyRow.IsMaterialized && copyRow.IsPresent,
                "the projected destination row must become a real one at completion");
            ctx.Assert.Equal(EntityPendingState.None, copyRow.PendingState,
                "the overlay must be gone once the bytes have landed");
            ctx.Assert.Equal(EntityPendingState.None, originalRow.PendingState,
                "a copy promises nothing about the file it reads");
        }

        await AssertNoActiveLedgerEntriesAsync(ctx, job.Id);
    }

    /// <summary>
    /// A terminal job must hold no active reservation (WP1 finding #5). Worth asserting here in
    /// particular: before step 15a an intra-volume job took none at all, so the release path had
    /// never run for one.
    /// </summary>
    internal static async Task AssertNoActiveLedgerEntriesAsync(ScenarioContext ctx, int jobId)
    {
        var active = await ctx.Env.WithDbAsync(async db =>
            await db.SpaceLedgerEntries.CountAsync(e => e.JobId == jobId && e.IsActive, ctx.Ct));
        ctx.Assert.Equal(0, active, "active ledger entries left by a terminal copy");
    }
}

/// <summary>
/// The cross-volume copy: the same state machine a cross-volume move travels, minus
/// <see cref="JobState.DeletingSource"/>. DeletingSource is the ONLY step that recycles anything,
/// so the source surviving is the assertion that the step was skipped.
/// </summary>
public sealed class CopyCrossVolumeScenario : Scenario
{
    public override string Name => "copy-cross-volume";

    public override string Description =>
        "Copy across volumes: file on both drives, source never recycled, destination indexed.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"tree\payload.bin";
        var sourceAbsolute = ctx.Source.CreateFile(relative, 64 * 1024);
        var sourceHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        // ── act ───────────────────────────────────────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = fileRow.Id,
            TargetVolumeId = ctx.TargetVolumeId,
            TargetRelativePath = ctx.Target.RelativePath(""),
        }, ctx.Ct);

        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state ({Harness.QueueDriver.Describe(finished)})");

        var landed = Path.Combine(ctx.Target.RootFullPath, "payload.bin");
        ctx.Assert.FileExists(landed, "the copy on the target drive");
        if (File.Exists(landed))
            ctx.Assert.Equal(sourceHash, ScenarioAssertions.Sha256(landed), "copied content across volumes");

        // The step that was skipped, observed by its absence: a cross-volume MOVE would have sent
        // this file to the recycle bin by now.
        ctx.Assert.FileExists(sourceAbsolute, "the source after a cross-volume COPY");
        if (File.Exists(sourceAbsolute))
            ctx.Assert.Equal(sourceHash, ScenarioAssertions.Sha256(sourceAbsolute), "source content after a copy");

        AssertNoPartialsAnywhere(ctx);

        var copyRow = await AssertCatalogHasFileAsync(
            ctx, ctx.TargetVolumeId, ctx.Target.RelativePath("payload.bin"), "the copy in the catalog");
        if (copyRow is not null)
        {
            ctx.Assert.Equal(ctx.TargetVolumeId, copyRow.VolumeId, "the copy belongs to the target volume");
            ctx.Assert.True(copyRow.IsMaterialized && copyRow.IsPresent, "the landed copy is materialized and present");
            // The FRN of a brand-new file is something only a scan can learn, and the unique
            // (VolumeId, UsnFileRef) index is filtered so nulls coexist.
            ctx.Assert.True(copyRow.UsnFileRef is null, "a freshly copied file has no FRN until a scan reads one");
        }

        // The source row must still describe the source volume — the trap ReconcileCancelledJob
        // and the move index update would fall into if a copy went through them.
        var originalRow = await AssertCatalogHasFileAsync(
            ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative), "the original after a cross-volume copy");
        if (originalRow is not null)
            ctx.Assert.Equal(ctx.SourceVolumeId, originalRow.VolumeId, "the original never changes volume");

        await CopyIntraVolumeScenario.AssertNoActiveLedgerEntriesAsync(ctx, job.Id);
    }
}

/// <summary>
/// A copy cancelled while the bytes are in flight. Three things must be true afterwards and the
/// third is new to this step: no <c>.fadit-partial</c>, the SOURCE untouched, and the projected
/// destination ROW gone from the catalog — it never stood for a file, so cleaning it up is a
/// delete, the one place §6's no-hard-delete does not apply.
/// </summary>
public sealed class CopyCancelledMidFlightScenario : Scenario
{
    public override string Name => "copy-cancel-mid-flight";

    public override string Description =>
        "Cancel during a copy: no partial, source intact, the projected destination row removed.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: big enough that the copy can be caught in flight ─────────
        const string relative = @"big\payload.bin";
        var sourceAbsolute = ctx.Source.CreateFile(relative, ctx.LargeFileBytes);
        var sourceHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        var job = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = fileRow.Id,
            TargetVolumeId = ctx.TargetVolumeId,
            TargetRelativePath = ctx.Target.RelativePath(""),
        }, ctx.Ct);

        // The projection exists the moment the job is queued (§5) — asserted before the cancel so
        // "the row is gone afterwards" means something.
        var projected = await AssertCatalogHasFileAsync(
            ctx, ctx.TargetVolumeId, ctx.Target.RelativePath("payload.bin"),
            "the projected destination of a queued copy");
        if (projected is not null)
        {
            ctx.Assert.True(!projected.IsMaterialized,
                "a queued copy's destination row must not claim the file exists yet");
            ctx.Assert.Equal(EntityPendingState.PendingCreate, projected.PendingState,
                "overlay state of a queued copy's destination");
        }

        // ── act ───────────────────────────────────────────────────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        await CancelMidCopyScenario.WaitUntilCopyingOrSkipAsync(ctx, job.Id);
        await ctx.Queue.CancelAsync(job.Id, ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Cancelled, finished.State,
            $"job state after cancel ({Harness.QueueDriver.Describe(finished)})");

        ctx.Assert.FileExists(sourceAbsolute, "the source after a cancelled copy");
        if (File.Exists(sourceAbsolute))
            ctx.Assert.Equal(sourceHash, ScenarioAssertions.Sha256(sourceAbsolute), "source content after a cancelled copy");

        AssertNoPartialsAnywhere(ctx);

        var stillThere = await FindFileRowAsync(
            ctx, ctx.TargetVolumeId, ctx.Target.RelativePath("payload.bin"));
        ctx.Assert.True(stillThere is null,
            "a cancelled copy must REMOVE its projected destination row, not blank its overlay: " +
            "that row never stood for a file, and leaving it behind would show the Catalog a file " +
            $"that will never exist. {(stillThere is null ? "" : await DescribeCatalogAsync(ctx))}");

        // The source row is untouched — never re-pointed at a destination the user just cancelled.
        var originalRow = await AssertCatalogHasFileAsync(
            ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative), "the original after a cancelled copy");
        if (originalRow is not null)
        {
            ctx.Assert.Equal(ctx.SourceVolumeId, originalRow.VolumeId, "the original never changes volume");
            ctx.Assert.Equal(EntityPendingState.None, originalRow.PendingState, "the original carries no overlay");
        }
    }
}
