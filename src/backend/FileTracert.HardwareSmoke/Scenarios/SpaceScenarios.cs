using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// §4: a job that does not fit is parked, never refused and never failed. It must stay
/// <c>Blocked(InsufficientSpace)</c> with the deficit recorded, and — crucially — must not have
/// started copying anything: no bytes on the target, no partial, source untouched.
///
/// Scarcity is arranged on the DEMAND side (the indexed size of the file the job carries), not by
/// lying about the drive's free space: since step 11b the check reads the device, so the stored
/// estimate is planted DELIBERATELY optimistic here — under the old code that number alone would
/// have let the job through and it would have died disk-full mid-copy.
/// </summary>
public sealed class InsufficientSpaceScenario : Scenario
{
    public override string Name => "insufficient-space";

    public override string Description =>
        "Cross-volume move bigger than the drive really has: Blocked(InsufficientSpace), not Failed, nothing copied.";

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

        // A demand the drive cannot possibly hold, whatever it really has right now…
        long freeNow = LiveFreeBytes(ctx, ctx.Target.Volume);
        long impossible = freeNow + (16L * 1024 * 1024 * 1024);
        await SetIndexedSizeAsync(ctx, fileRow.Id, impossible);
        // …while the catalog's stale estimate says there is room to spare. Only a check that
        // asks the device can get this right.
        await SetVolumeFreeBytesAsync(ctx, ctx.TargetVolumeId, impossible * 2);
        ctx.Log($"target free now {freeNow:N0} B; demand arranged at {impossible:N0} B");

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
/// Finding 10 on real hardware: the room a job needs must be checked against the DRIVE at the
/// moment of execution, not against the estimate the last volume sync wrote. The job is enqueued
/// while it fits; the demand then grows past what the volume really holds — the harness's stand-in
/// for another process filling the drive, which moves the same side of the same comparison without
/// having to take a 300 GB volume down to zero for a few seconds. The engine must park it instead
/// of starting a copy that would die disk-full halfway; when the pressure goes away it must run on
/// its own, with the file intact.
/// </summary>
public sealed class LiveSpaceRecheckScenario : Scenario
{
    public override string Name => "live-space-recheck";

    public override string Description =>
        "Room disappears after the enqueue: the execution re-check parks the job, then it recovers on its own.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"live\payload.bin";
        const long size = 4 * 1024 * 1024;
        var sourceAbsolute = ctx.Source.CreateFile(relative, size);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        // Enqueued while it fits — and it does, the file is 4 MB.
        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);
        ctx.Assert.Equal("Pending", job.State, "state at enqueue, with the room really there");

        // Now the room is gone. The stored estimate is left saying the opposite on purpose: a
        // check that reads it instead of the device would copy and run out of disk mid-file.
        long freeNow = LiveFreeBytes(ctx, ctx.Target.Volume);
        long impossible = freeNow + (16L * 1024 * 1024 * 1024);
        await SetJobRequiredBytesAsync(ctx, job.Id, impossible);
        await SetVolumeFreeBytesAsync(ctx, ctx.TargetVolumeId, impossible * 2);
        ctx.Log($"target free now {freeNow:N0} B; job demand raised to {impossible:N0} B");

        // ── act 1: the worker picks it up and must refuse to copy ─────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var blocked = await ctx.Queue.WaitAsync(
            job.Id,
            j => j.State == JobState.Blocked,
            ctx.Timeout,
            "the execution re-check to park the job that no longer fits",
            ctx.Ct);

        ctx.Assert.Equal(JobBlockReason.InsufficientSpace, blocked.BlockReason,
            $"block reason ({Harness.QueueDriver.Describe(blocked)})");
        ctx.Assert.FileExists(sourceAbsolute, "source of a job the re-check refused");
        ctx.Assert.FileMissing(
            Path.Combine(ctx.Target.RootFullPath, "payload.bin"),
            "a job parked by the re-check must not have copied anything");
        AssertNoPartialsAnywhere(ctx);

        // ── act 2: the pressure goes away → it runs by itself ─────────────────
        await SetJobRequiredBytesAsync(ctx, job.Id, size);
        await OfflineSimulatedScenario.RevaluateAndSignalAsync(ctx);

        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        // ── assert 2 ──────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"state once the room is back ({Harness.QueueDriver.Describe(finished)})");
        ctx.Assert.Equal(0, finished.RetryCount, "retry count (it must recover without a manual retry)");

        var targetAbsolute = Path.Combine(ctx.Target.RootFullPath, "payload.bin");
        ctx.Assert.FileExists(targetAbsolute, "file on the target after the recovery");
        if (File.Exists(targetAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(targetAbsolute), "file content after the recovery");
        ctx.Assert.FileMissing(sourceAbsolute, "source after the completed move");
        AssertNoPartialsAnywhere(ctx);
    }
}

/// <summary>
/// §4 asks for the hard check to demand "free space + margin (2–5%)", configured in
/// <c>AppSettings.SpaceMarginPercent</c>. On real hardware: a job sized to fit the drive exactly
/// must be parked while the margin is on, and run once the margin is zero — the knob has to move
/// something, or it is decoration.
/// </summary>
public sealed class SpaceMarginScenario : Scenario
{
    public override string Name => "space-margin";

    public override string Description =>
        "A move that fits exactly is parked by the safety margin, and runs once the margin is zero.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"margin\payload.bin";
        const long size = 2 * 1024 * 1024;
        var sourceAbsolute = ctx.Source.CreateFile(relative, size);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);

        // A demand of exactly what the drive has free: it fits, and nothing but the margin can
        // stand in its way. 10% instead of the seeded 3% so the arrangement survives the handful
        // of bytes the drive may gain or lose between the probe and the check.
        long freeNow = LiveFreeBytes(ctx, ctx.Target.Volume);
        long exactlyFull = (long)(freeNow * 0.95);
        await SetSpaceMarginPercentAsync(ctx, 10);
        await SetJobRequiredBytesAsync(ctx, job.Id, exactlyFull);
        ctx.Log($"target free now {freeNow:N0} B; demand {exactlyFull:N0} B; margin 10%");

        // ── act 1: the margin is what refuses it ──────────────────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var blocked = await ctx.Queue.WaitAsync(
            job.Id,
            j => j.State == JobState.Blocked,
            ctx.Timeout,
            "the safety margin to park a job that would otherwise fit",
            ctx.Ct);

        ctx.Assert.Equal(JobBlockReason.InsufficientSpace, blocked.BlockReason,
            $"block reason ({Harness.QueueDriver.Describe(blocked)})");
        ctx.Assert.True(
            (blocked.ErrorMessage ?? "").Contains("margin", StringComparison.OrdinalIgnoreCase),
            $"the message must name the cushion that refused the job (was: '{blocked.ErrorMessage}')");
        ctx.Assert.FileMissing(
            Path.Combine(ctx.Target.RootFullPath, "payload.bin"),
            "a job parked by the margin must not have copied anything");

        // ── act 2: knob to zero → the same job runs ───────────────────────────
        await SetSpaceMarginPercentAsync(ctx, 0);
        await SetJobRequiredBytesAsync(ctx, job.Id, size);
        await OfflineSimulatedScenario.RevaluateAndSignalAsync(ctx);

        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        // ── assert 2 ──────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"state with the margin at zero ({Harness.QueueDriver.Describe(finished)})");

        var targetAbsolute = Path.Combine(ctx.Target.RootFullPath, "payload.bin");
        ctx.Assert.FileExists(targetAbsolute, "file on the target once the margin allows it");
        if (File.Exists(targetAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(targetAbsolute), "file content");
        AssertNoPartialsAnywhere(ctx);
    }
}

/// <summary>
/// §4 FIFO recovery: job B cannot run because job A, ahead of it in the queue, has already claimed
/// the room on the target. B must reach Completed on its own — no manual retry — the moment A's
/// claim is released. The two areas of a cross pair sit on different drives, but the room A holds
/// is ledger bookkeeping, not physics: what this proves is that a queued promise is honoured while
/// it stands and forgotten when it ends.
/// </summary>
public sealed class FifoAutoRecoveryScenario : Scenario
{
    public override string Name => "fifo-auto-recovery";

    public override string Description =>
        "Job B, blocked behind the room job A reserved, completes on its own after A — without a manual retry.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        const long size = 4 * 1024 * 1024;

        // ── arrange: two files on the source, both indexed ────────────────────
        var firstAbsolute = ctx.Source.CreateFile(@"incoming\first.bin", size);
        var secondAbsolute = ctx.Source.CreateFile(@"incoming\second.bin", size);
        var secondHash = ScenarioAssertions.Sha256(secondAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());

        var firstRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(@"incoming\first.bin"));
        var secondRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(@"incoming\second.bin"));
        if (firstRow is null || secondRow is null)
        {
            ctx.Assert.Fail("arrange failed: one of the two fixtures was not indexed.");
            return;
        }

        // ── act: A takes almost everything the target has, B follows ──────────
        long freeNow = LiveFreeBytes(ctx, ctx.Target.Volume);
        long almostEverything = (long)(freeNow * 0.9);
        await SetIndexedSizeAsync(ctx, firstRow.Id, almostEverything);

        var jobA = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, firstRow.Id, "reserved"), ctx.Ct);
        // B needs a fifth of the drive: it fits on its own, never behind A's claim.
        await SetIndexedSizeAsync(ctx, secondRow.Id, freeNow / 5);
        var jobB = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, secondRow.Id, "landed"), ctx.Ct);

        ctx.Assert.Equal("Blocked", jobB.State, "job B at enqueue, behind A's reservation");

        // A's real payload is 4 MB — the demand was the point, the copy is ordinary work.
        await SetJobRequiredBytesAsync(ctx, jobA.Id, size);

        await ctx.Queue.StartWorkerAsync(ctx.Ct);

        var finishedA = await ctx.Queue.WaitForTerminalAsync(jobA.Id, ctx.Timeout, ctx.Ct);
        var finishedB = await ctx.Queue.WaitAsync(
            jobB.Id,
            j => j.State == JobState.Completed,
            ctx.Timeout,
            "job B to complete on its own once A released the room it had claimed",
            ctx.Ct);

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finishedA.State,
            $"job A state ({Harness.QueueDriver.Describe(finishedA)})");
        ctx.Assert.Equal(JobState.Completed, finishedB.State,
            $"job B state ({Harness.QueueDriver.Describe(finishedB)})");
        ctx.Assert.Equal(0, finishedB.RetryCount, "job B retry count (it must recover without a manual retry)");

        ctx.Assert.FileMissing(firstAbsolute, "job A's source after it completed");
        ctx.Assert.FileExists(
            Path.Combine(ctx.Target.RootFullPath, "reserved", "first.bin"), "job A's file on its destination");

        ctx.Assert.FileMissing(secondAbsolute, "job B's source after it completed");
        var landed = Path.Combine(ctx.Target.RootFullPath, "landed", "second.bin");
        ctx.Assert.FileExists(landed, "job B's file on its destination");
        if (File.Exists(landed))
            ctx.Assert.Equal(secondHash, ScenarioAssertions.Sha256(landed), "job B's file content");

        AssertNoPartialsAnywhere(ctx);
    }
}
