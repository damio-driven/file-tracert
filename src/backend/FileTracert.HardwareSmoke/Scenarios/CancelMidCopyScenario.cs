using FileTracert.Contracts.Enums;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Cancels a cross-volume move while the copy is in flight. The contract (§4): the source is never
/// touched before a verified copy exists, so a cancel must leave it byte-identical, must not leave
/// the half-written <c>.fadit-partial</c> behind, and must not publish a final file on the target.
/// </summary>
public sealed class CancelMidCopyScenario : Scenario
{
    public override string Name => "cancel-mid-copy";

    public override string Description =>
        "Cancel during the copy: source intact, no partial, no target file, job Cancelled.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: a file big enough that the copy can be caught in flight ──
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

        // ── act ───────────────────────────────────────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);

        await WaitUntilCopyingOrSkipAsync(ctx, job.Id);
        await ctx.Queue.CancelAsync(job.Id, ctx.Ct);

        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        // The engine cleans partials on its own cancel path; give the in-flight job a moment to
        // unwind before inspecting the disk, otherwise we would be racing its own cleanup.
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Cancelled, finished.State,
            $"job state after cancel ({Harness.QueueDriver.Describe(finished)})");

        ctx.Assert.FileExists(sourceAbsolute, "source after a cancelled move");
        if (File.Exists(sourceAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(sourceAbsolute), "source content after a cancelled move");

        ctx.Assert.FileMissing(
            Path.Combine(ctx.Target.RootFullPath, "payload.bin"),
            "a cancelled move must not publish a final file on the target");

        AssertNoPartialsAnywhere(ctx);
    }

    /// <summary>
    /// Waits for the job to actually be copying. If it finished first, the fixture is too small
    /// for this machine to interrupt — that is a SKIP with a concrete remedy, not a pass.
    /// </summary>
    internal static async Task WaitUntilCopyingOrSkipAsync(ScenarioContext ctx, int jobId)
    {
        var deadline = DateTime.UtcNow + ctx.Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var job = await ctx.Queue.LoadJobAsync(jobId, ctx.Ct);

            if (job.Items.Any(i => i.State == JobItemState.Copying))
                return;

            if (Harness.QueueDriver.IsTerminal(job.State) ||
                job.State is JobState.Verifying or JobState.DeletingSource)
            {
                throw new ScenarioSkippedException(
                    $"the copy reached {job.State} before it could be interrupted — " +
                    $"raise HardwareSmoke:LargeFileMegabytes above {ctx.Options.LargeFileMegabytes}.");
            }

            await Task.Delay(5, ctx.Ct);
        }

        throw new TimeoutException("the job never started copying.");
    }
}
