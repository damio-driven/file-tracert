using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Scenarios;

// WP1 (finding #4) — crash/resume at the three steps that used to fail against their own prior
// work. Each scenario ENQUEUES through the real service, then forges on disk + in the DB the
// exact footprint the documented crash leaves behind, and finally starts the real worker: the
// resume path under test is the production one end-to-end.

/// <summary>
/// Crash between <c>FinalizePartial</c> (partial→final rename) and the <c>Verified</c> checkpoint:
/// the DB still says Copied+TempPath while the final file already sits on the target. The resumed
/// worker must verify the final in place and finish — not fail on the missing partial.
/// </summary>
public sealed class CrashResumeVerifyingScenario : Scenario
{
    public override string Name => "crash-resume-verifying";

    public override string Description =>
        "Crash after finalize, before the Verified checkpoint: the resume verifies in place and completes.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: real enqueue, then the forged crash footprint ────────────
        const string relative = @"docs\report.bin";
        var sourceAbsolute = ctx.Source.CreateFile(relative, 64 * 1024);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);

        var targetAbsolute = Path.Combine(ctx.Target.RootFullPath, "report.bin");
        File.Copy(sourceAbsolute, targetAbsolute); // the rename already happened; no partial left
        await ctx.Env.WithDbAsync(async db =>
        {
            var row = await db.OperationJobs.Include(j => j.Items).FirstAsync(j => j.Id == job.Id, ctx.Ct);
            row.State = JobState.Verifying;
            row.StartedUtc = DateTime.UtcNow;
            row.BytesProcessed = row.TotalBytes;
            var item = row.Items.Single();
            item.State = JobItemState.Copied;
            item.TempPath = item.TargetRelativePath + ".fadit-partial";
            item.BytesCopied = item.SizeBytes;
            await db.SaveChangesAsync(ctx.Ct);
        });
        ctx.Log("forged crash footprint: final on target, checkpoint still Copied@Verifying");

        // ── act: a fresh worker resumes ───────────────────────────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state after resume ({Harness.QueueDriver.Describe(finished)})");
        ctx.Assert.FileExists(targetAbsolute, "final file on the target");
        if (File.Exists(targetAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(targetAbsolute), "final content");
        ctx.Assert.FileMissing(sourceAbsolute, "source after the resumed move completed");
        AssertNoPartialsAnywhere(ctx);

        await AssertCatalogHasFileAsync(ctx, ctx.TargetVolumeId,
            ctx.Target.RelativePath("report.bin"), "catalog after the resumed completion");
    }
}

/// <summary>
/// Crash mid recycle loop of a cross-volume MoveFolder: one source already recycled, its item
/// still Verified in the DB. The resume must treat the missing source as done, recycle the rest
/// and complete — not throw on the path recycled by the previous run.
/// </summary>
public sealed class CrashResumeDeletingSourceScenario : Scenario
{
    public override string Name => "crash-resume-deleting-source";

    public override string Description =>
        "Crash mid source-recycle of a MoveFolder: the resume tolerates the already-recycled source.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        var srcX = ctx.Source.CreateFile(@"band\x.bin", 32 * 1024);
        var srcY = ctx.Source.CreateFile(@"band\y.bin", 32 * 1024);
        var hashX = ScenarioAssertions.Sha256(srcX);
        var hashY = ScenarioAssertions.Sha256(srcY);

        await ctx.IndexSourceAsync(AllowEverything());
        var dirRow = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath("band"));
        if (dirRow is null)
        {
            ctx.Assert.Fail("arrange failed: no catalog row for the 'band' directory.");
            return;
        }

        var job = await ctx.Queue.EnqueueAsync(MoveFolderTo(ctx, dirRow.Id), ctx.Ct);

        // Forge: everything copied + finalized; x's source already recycled by the dying run.
        var dstDir = Path.Combine(ctx.Target.RootFullPath, "band");
        Directory.CreateDirectory(dstDir);
        File.Copy(srcX, Path.Combine(dstDir, "x.bin"));
        File.Copy(srcY, Path.Combine(dstDir, "y.bin"));
        File.Delete(srcX);
        await ctx.Env.WithDbAsync(async db =>
        {
            var row = await db.OperationJobs.Include(j => j.Items).FirstAsync(j => j.Id == job.Id, ctx.Ct);
            row.State = JobState.DeletingSource;
            row.StartedUtc = DateTime.UtcNow;
            row.BytesProcessed = row.TotalBytes;
            foreach (var item in row.Items)
            {
                // Folder marker (FileId null) was completed during the copy phase.
                item.State = item.FileId is null ? JobItemState.Done : JobItemState.Verified;
                item.BytesCopied = item.SizeBytes;
            }
            await db.SaveChangesAsync(ctx.Ct);
        });
        ctx.Log("forged crash footprint: x recycled + still Verified, y untouched, job@DeletingSource");

        // ── act ───────────────────────────────────────────────────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state after resume ({Harness.QueueDriver.Describe(finished)})");
        ctx.Assert.FileMissing(srcY, "remaining source after the resumed delete");
        ctx.Assert.Equal(hashX, ScenarioAssertions.Sha256(Path.Combine(dstDir, "x.bin")), "x content on target");
        ctx.Assert.Equal(hashY, ScenarioAssertions.Sha256(Path.Combine(dstDir, "y.bin")), "y content on target");
        ctx.Assert.True(!Directory.Exists(ctx.Source.FullPath("band")),
            "emptied source folder must be recycled by the resumed run");
        AssertNoPartialsAnywhere(ctx);

        await AssertCatalogHasFileAsync(ctx, ctx.TargetVolumeId,
            ctx.Target.RelativePath(@"band\y.bin"), "catalog after the resumed completion");
    }
}

/// <summary>
/// Crash right after the single un-checkpointed OS call of an intra-volume move: the job is still
/// Pending but the file already sits at the target. The re-run must recognize the applied op and
/// complete (index update included) — not fail with FileNotFound on its own success.
/// </summary>
public sealed class CrashResumeSimpleOpScenario : Scenario
{
    public override string Name => "crash-resume-simple-op";

    public override string Description =>
        "Crash after an intra-volume File.Move: the re-run detects 'already applied' and completes.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: intra-volume move inside the SOURCE area (works on any pair) ──
        const string relative = @"orig\pic.bin";
        var sourceAbsolute = ctx.Source.CreateFile(relative, 16 * 1024);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        var job = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = fileRow.Id,
            TargetVolumeId = ctx.SourceVolumeId,
            TargetRelativePath = ctx.Source.RelativePath("moved"),
        }, ctx.Ct);

        // Forge: the OS call already ran; the process died before any checkpoint.
        var targetAbsolute = ctx.Source.FullPath(@"moved\pic.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute)!);
        File.Move(sourceAbsolute, targetAbsolute);
        await ctx.Env.WithDbAsync(async db =>
        {
            var row = await db.OperationJobs.FirstAsync(j => j.Id == job.Id, ctx.Ct);
            row.StartedUtc = DateTime.UtcNow; // the interrupted run had started
            await db.SaveChangesAsync(ctx.Ct);
        });
        ctx.Log("forged crash footprint: file physically moved, job still Pending");

        // ── act ───────────────────────────────────────────────────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state after re-run ({Harness.QueueDriver.Describe(finished)})");
        ctx.Assert.FileExists(targetAbsolute, "moved file at its target");
        if (File.Exists(targetAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(targetAbsolute), "moved file content");
        ctx.Assert.FileMissing(sourceAbsolute, "original path after the move");

        await AssertCatalogHasFileAsync(ctx, ctx.SourceVolumeId,
            ctx.Source.RelativePath(@"moved\pic.bin"), "catalog re-pointed by the re-run");
    }
}
