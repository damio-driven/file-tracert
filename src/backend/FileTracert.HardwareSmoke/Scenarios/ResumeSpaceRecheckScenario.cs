using FileTracert.Contracts.Enums;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Step 15b — a job interrupted mid-copy and picked up again must ask the drive BEFORE it writes
/// another byte. The hard re-check used to live only in the <see cref="JobState.Pending"/> branch,
/// so a resumed job walked back into the copy on the strength of an answer given before the
/// interruption.
///
/// <para>Scarcity is arranged on the DEMAND side, exactly as <c>insufficient-space</c> does it and
/// for the same reason step 11b wrote down: filling a real drive — here the system disk — for a
/// few seconds is a disservice, not a test, and a process killed halfway through would leave it
/// full. Moving either side of "demand vs live free space" exercises the same branch.</para>
///
/// <para>What makes this scenario worth its runtime rather than a duplicate of
/// <c>insufficient-space</c>: the demand is raised only AFTER the job has already been running and
/// has bytes on the target. That is the state the old code never re-examined.</para>
/// </summary>
public sealed class ResumeSpaceRecheckScenario : Scenario
{
    public override string Name => "resume-space-recheck";

    public override string Description =>
        "Job interrupted mid-copy, drive no longer big enough: the resume is refused, not attempted.";

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

        // ── act: start it, interrupt it mid-copy ──────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);

        await CancelMidCopyScenario.WaitUntilCopyingOrSkipAsync(ctx, job.Id);
        await ctx.Queue.StopWorkerAsync();
        ctx.Log("worker stopped mid-copy (simulated crash)");

        var checkpointed = await ctx.Queue.LoadJobAsync(job.Id, ctx.Ct);
        ctx.Assert.True(
            !Harness.QueueDriver.IsTerminal(checkpointed.State),
            $"arrange: an interrupted job must stay runnable ({Harness.QueueDriver.Describe(checkpointed)})");

        // The drive "fills up" while the job sits at its checkpoint. Demand-side, per the class
        // comment; the catalog's stale estimate is left saying there is room, so only a check that
        // asks the DEVICE can get this right.
        long freeNow = LiveFreeBytes(ctx, ctx.Target.Volume);
        long impossible = freeNow + (16L * 1024 * 1024 * 1024);
        await SetJobRequiredBytesAsync(ctx, job.Id, impossible);
        await SetVolumeFreeBytesAsync(ctx, ctx.TargetVolumeId, impossible * 2);
        ctx.Log($"target free now {freeNow:N0} B; demand raised to {impossible:N0} B while parked");

        long bytesOnTargetBefore = TargetBytes(ctx);

        // ── act: hand it back to the worker ───────────────────────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var settled = await ctx.Queue.WaitAsync(job.Id,
            j => j.State is JobState.Blocked or JobState.Failed or JobState.Completed,
            ctx.Timeout, "the resumed job to settle", ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Blocked, settled.State,
            $"a resume must re-ask the drive and park when it no longer fits, never Failed " +
            $"({Harness.QueueDriver.Describe(settled)})");
        ctx.Assert.Equal(JobBlockReason.InsufficientSpace, settled.BlockReason,
            "block reason of the refused resume");

        // The point is not the label on the job, it is that no further byte was written.
        ctx.Assert.Equal(bytesOnTargetBefore, TargetBytes(ctx),
            "a refused resume must not have copied one byte more");

        // §4 — recoverable, so the source is untouched and the job can run again later.
        ctx.Assert.FileExists(sourceAbsolute, "source of a job whose resume was refused");
        if (File.Exists(sourceAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(sourceAbsolute),
                "source content of a job whose resume was refused");

        ctx.Assert.FileMissing(
            Path.Combine(ctx.Target.RootFullPath, "payload.bin"),
            "a refused resume must not have published a final file");
    }

    /// <summary>Every byte currently sitting in the target area, partials included.</summary>
    private static long TargetBytes(ScenarioContext ctx)
    {
        var root = ctx.Target.RootFullPath;
        if (!Directory.Exists(root)) return 0;

        long total = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(path).Length; }
            catch (IOException) { /* a file that vanished under us contributes nothing */ }
        }
        return total;
    }
}
