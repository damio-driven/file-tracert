using FileTracert.Contracts.Enums;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// WP1 — crash and resume. The worker is stopped while a cross-volume copy is in flight, exactly
/// as a service shutdown or a kill would interrupt it: the job stays at its persisted checkpoint.
/// A fresh worker must then finish the job cleanly — no double-counted progress, no orphan
/// <c>.fadit-partial</c>, no file left half-written, and the source only released at the end.
/// </summary>
public sealed class CrashResumeScenario : Scenario
{
    public override string Name => "crash-resume-mid-copy";

    public override string Description =>
        "Worker killed mid-copy, then restarted: the job resumes and completes with clean 100%.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"big\payload.bin";
        var sourceAbsolute = ctx.Source.CreateFile(relative, ctx.LargeFileBytes);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        // ── act: start, interrupt mid-copy, restart ───────────────────────────
        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);

        await CancelMidCopyScenario.WaitUntilCopyingOrSkipAsync(ctx, job.Id);
        await ctx.Queue.StopWorkerAsync();
        ctx.Log("worker stopped mid-copy (simulated crash)");

        var checkpointed = await ctx.Queue.LoadJobAsync(job.Id, ctx.Ct);
        ctx.Assert.True(
            !Harness.QueueDriver.IsTerminal(checkpointed.State),
            $"an interrupted job must stay runnable, not reach a terminal state " +
            $"({Harness.QueueDriver.Describe(checkpointed)})");

        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state after resume ({Harness.QueueDriver.Describe(finished)})");

        ctx.Assert.Equal(finished.TotalBytes, finished.BytesProcessed,
            "progress after resume must land on exactly 100% (no double-counted bytes)");

        var targetAbsolute = Path.Combine(ctx.Target.RootFullPath, "payload.bin");
        ctx.Assert.FileExists(targetAbsolute, "resumed file on the target");
        if (File.Exists(targetAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(targetAbsolute), "resumed file content");

        ctx.Assert.FileMissing(sourceAbsolute, "source after the resumed move completed");
        AssertNoPartialsAnywhere(ctx);
    }
}
