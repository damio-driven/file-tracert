using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// §4: a job that does not fit is parked, never refused and never failed. It must stay
/// <c>Blocked(InsufficientSpace)</c> with the deficit recorded, and — crucially — must not have
/// started copying anything: no bytes on the target, no partial, source untouched.
/// </summary>
public sealed class InsufficientSpaceScenario : Scenario
{
    public override string Name => "insufficient-space";

    public override string Description =>
        "Cross-volume move that does not fit: Blocked(InsufficientSpace), not Failed, nothing copied.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"big\payload.bin";
        const long size = 8 * 1024 * 1024;
        var sourceAbsolute = ctx.Source.CreateFile(relative, size);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        // Half the demand: the job can never fit, whatever the drive really has.
        await SetVolumeFreeBytesAsync(ctx, ctx.TargetVolumeId, size / 2);

        // ── act ───────────────────────────────────────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);

        // Give the worker real chances to pick it up: a Blocked job must be invisible to it.
        await Task.Delay(TimeSpan.FromSeconds(2), ctx.Ct);
        var observed = await ctx.Queue.LoadJobAsync(job.Id, ctx.Ct);

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Blocked, observed.State,
            $"job state ({Harness.QueueDriver.Describe(observed)})");
        ctx.Assert.Equal(JobBlockReason.InsufficientSpace, observed.BlockReason, "block reason");

        ctx.Assert.FileExists(sourceAbsolute, "source of a blocked job");
        if (File.Exists(sourceAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(sourceAbsolute), "source content of a blocked job");

        ctx.Assert.FileMissing(
            Path.Combine(ctx.Target.RootFullPath, "payload.bin"),
            "a blocked job must not have copied anything");
        AssertNoPartialsAnywhere(ctx);
    }
}

/// <summary>
/// §4 FIFO recovery: job A frees space on the target volume, job B needs exactly that space. B is
/// enqueued behind A on a target with nothing free, and must reach Completed on its own — no
/// manual retry — once A's liberation materializes and the re-evaluation wakes it.
/// </summary>
public sealed class FifoAutoRecoveryScenario : Scenario
{
    public override string Name => "fifo-auto-recovery";

    public override string Description =>
        "Job A frees the space job B needs: B completes on its own after A, without a manual retry.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        const long size = 4 * 1024 * 1024;

        // ── arrange: one file on each side, both indexed ──────────────────────
        var freedAbsolute = ctx.Target.CreateFile(@"outgoing\freed.bin", size);
        var neededAbsolute = ctx.Source.CreateFile(@"incoming\needed.bin", size);
        var neededHash = ScenarioAssertions.Sha256(neededAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        await ctx.IndexAsync(ctx.Target, ctx.TargetVolumeId, AllowEverything());

        var freedRow = await FindFileRowAsync(ctx, ctx.TargetVolumeId, ctx.Target.RelativePath(@"outgoing\freed.bin"));
        var neededRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(@"incoming\needed.bin"));
        if (freedRow is null || neededRow is null)
        {
            ctx.Assert.Fail("arrange failed: one of the two fixtures was not indexed.");
            return;
        }

        // Nothing free on the target: B can only ever fit thanks to A's liberation.
        await SetVolumeFreeBytesAsync(ctx, ctx.TargetVolumeId, 0);

        // ── act: A (target → source, frees the target) then B (source → target)
        var jobA = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = freedRow.Id,
            TargetVolumeId = ctx.SourceVolumeId,
            TargetRelativePath = ctx.Source.RelativePath("recovered"),
        }, ctx.Ct);

        var jobB = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, neededRow.Id, "landed"), ctx.Ct);

        await ctx.Queue.StartWorkerAsync(ctx.Ct);

        var finishedA = await ctx.Queue.WaitForTerminalAsync(jobA.Id, ctx.Timeout, ctx.Ct);
        var finishedB = await ctx.Queue.WaitAsync(
            jobB.Id,
            j => j.State == JobState.Completed,
            ctx.Timeout,
            "job B to complete on its own after A freed the space",
            ctx.Ct);

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finishedA.State,
            $"job A state ({Harness.QueueDriver.Describe(finishedA)})");
        ctx.Assert.Equal(JobState.Completed, finishedB.State,
            $"job B state ({Harness.QueueDriver.Describe(finishedB)})");
        ctx.Assert.Equal(0, finishedB.RetryCount, "job B retry count (it must recover without a manual retry)");

        ctx.Assert.FileMissing(freedAbsolute, "job A's source after it completed");
        ctx.Assert.FileExists(ctx.Source.FullPath(@"recovered\freed.bin"), "job A's file on its destination");

        ctx.Assert.FileMissing(neededAbsolute, "job B's source after it completed");
        var landed = Path.Combine(ctx.Target.RootFullPath, "landed", "needed.bin");
        ctx.Assert.FileExists(landed, "job B's file on its destination");
        if (File.Exists(landed))
            ctx.Assert.Equal(neededHash, ScenarioAssertions.Sha256(landed), "job B's file content");

        AssertNoPartialsAnywhere(ctx);
    }
}
