using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// The product's central promise (§1): a job queued against a drive that is not there waits for it
/// instead of dying. This scenario tests the LOGIC — the volume is marked offline in the catalog
/// while it is still physically mounted, so nothing but the queue's own gate can stop the job.
/// A parked job (Pending, or Blocked with an offline reason) is correct; Completed means the gate
/// does not exist, Failed means the promise is broken.
/// </summary>
public sealed class OfflineSimulatedScenario : Scenario
{
    public override string Name => "offline-simulated";

    public override string Description =>
        "Target volume marked offline: the job waits (never Failed) and runs by itself once it is back online.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"pending\payload.bin";
        var sourceAbsolute = ctx.Source.CreateFile(relative, 2 * 1024 * 1024);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        await SetVolumeOnlineAsync(ctx, ctx.TargetVolumeId, isOnline: false);

        // ── act: enqueue against an "offline" target ──────────────────────────
        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ctx.Ct);

        var whileOffline = await ctx.Queue.LoadJobAsync(job.Id, ctx.Ct);

        // ── assert: parked, not resolved one way or the other ─────────────────
        ctx.Assert.True(
            whileOffline.State != JobState.Failed,
            $"a job whose target volume is offline must never fail ({Harness.QueueDriver.Describe(whileOffline)})");

        ctx.Assert.True(
            whileOffline.State != JobState.Completed,
            "the queue must not execute a job against a volume the catalog reports offline " +
            $"({Harness.QueueDriver.Describe(whileOffline)})");

        if (Harness.QueueDriver.IsTerminal(whileOffline.State))
        {
            ctx.Log("job already terminal while offline — the second half of the scenario cannot run.");
            return;
        }

        ctx.Log($"while offline: {Harness.QueueDriver.Describe(whileOffline)}");

        // ── act: bring the volume back ────────────────────────────────────────
        await SetVolumeOnlineAsync(ctx, ctx.TargetVolumeId, isOnline: true);
        await RevaluateAndSignalAsync(ctx);

        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        // ── assert: it ran by itself once the volume came back ────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state after the volume came back online ({Harness.QueueDriver.Describe(finished)})");

        var targetAbsolute = Path.Combine(ctx.Target.RootFullPath, "payload.bin");
        ctx.Assert.FileExists(targetAbsolute, "file on the target after the volume came back");
        if (File.Exists(targetAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(targetAbsolute), "file content after the remount");

        AssertNoPartialsAnywhere(ctx);
    }

    /// <summary>
    /// Wakes the queue the way a mount event should: re-evaluate the parked jobs, then signal the
    /// processor. Both go through the product's own services.
    /// </summary>
    internal static async Task RevaluateAndSignalAsync(ScenarioContext ctx)
    {
        await ctx.Env.WithScopeAsync<object?>(async sp =>
        {
            await sp.GetRequiredService<Business.Operations.BlockedJobRevaluator>().RevaluateAsync(ctx.Ct);
            return null;
        });

        ctx.Env.Services.GetRequiredService<IQueueSignal>().Signal();
    }
}

/// <summary>
/// The same promise, tested against the metal: the operator physically unplugs the external drive,
/// the harness reflects the probe's verdict into the catalog exactly as the volume sync does, and
/// the job must survive the disconnection and complete after the drive is plugged back in —
/// even if Windows gives it a different drive letter, because the queue keys on the volume GUID.
/// Only runs with <c>SemiAutomatic=true</c> and an External target.
/// </summary>
public sealed class OfflineUnplugScenario : Scenario
{
    public override string Name => "offline-unplug";

    public override string Description =>
        "Operator unplugs the target drive: the job waits, then completes after the drive comes back.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override bool NeedsSemiAutomatic => true;

    public override bool NeedsExternalTarget => true;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"pending\payload.bin";
        var sourceAbsolute = ctx.Source.CreateFile(relative, 2 * 1024 * 1024);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        var probe = ctx.Env.Services.GetRequiredService<IVolumeProbe>();
        var guid = ctx.Pair.Target.VolumeGuid;

        ctx.Console.WaitForOperator(
            $"Stacca fisicamente il volume '{ctx.Pair.Target.Name}' ({ctx.Pair.Target.MountPoint}).");

        if (IsMounted(probe, guid))
            throw new ScenarioSkippedException(
                $"volume '{ctx.Pair.Target.Name}' is still mounted — the drive was not actually removed.");

        await SyncOnlineStateFromProbeAsync(ctx, probe, guid);

        // ── act: enqueue with the drive gone ──────────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ctx.Ct);

        var whileUnplugged = await ctx.Queue.LoadJobAsync(job.Id, ctx.Ct);
        ctx.Assert.True(
            whileUnplugged.State != JobState.Failed,
            $"an unplugged target volume must park the job, not fail it ({Harness.QueueDriver.Describe(whileUnplugged)})");
        ctx.Assert.True(
            whileUnplugged.State != JobState.Completed,
            $"the job cannot have completed with the drive unplugged ({Harness.QueueDriver.Describe(whileUnplugged)})");

        ctx.Log($"while unplugged: {Harness.QueueDriver.Describe(whileUnplugged)}");

        // ── act: plug it back in ──────────────────────────────────────────────
        ctx.Console.WaitForOperator($"Ricollega il volume '{ctx.Pair.Target.Name}'.");

        var remounted = await WaitForMountAsync(probe, guid, TimeSpan.FromSeconds(60), ctx.Ct)
            ?? throw new ScenarioSkippedException(
                $"volume '{ctx.Pair.Target.Name}' did not come back within 60s — cannot finish the scenario.");

        await SyncOnlineStateFromProbeAsync(ctx, probe, guid);

        if (Harness.QueueDriver.IsTerminal(whileUnplugged.State))
        {
            ctx.Log("job was already terminal while unplugged — the remount half cannot run.");
            return;
        }

        await OfflineSimulatedScenario.RevaluateAndSignalAsync(ctx);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        // ── assert: it ran after the remount, at whatever mount point ─────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state after the remount ({Harness.QueueDriver.Describe(finished)})");

        var mountNow = remounted.MountPoints[0];
        if (!string.Equals(mountNow, ctx.Pair.Target.MountPoint, StringComparison.OrdinalIgnoreCase))
            ctx.Log($"the drive came back at '{mountNow}' instead of '{ctx.Pair.Target.MountPoint}' — the job followed the GUID.");

        var targetAbsolute = Path.Combine(mountNow, ctx.Target.RootRelativePath, "payload.bin");
        ctx.Assert.FileExists(targetAbsolute, "file on the target after the remount");
        if (File.Exists(targetAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(targetAbsolute), "file content after the remount");

        ctx.Assert.FileMissing(sourceAbsolute, "source after the completed move");
        ctx.Assert.NoPartialsUnder(Path.Combine(mountNow, ctx.Target.RootRelativePath), "target area after the remount");
    }

    private static bool IsMounted(IVolumeProbe probe, string volumeGuid) =>
        probe.TryGetByGuid(volumeGuid) is { MountPoints.Count: > 0 };

    private static async Task<ProbedVolume?> WaitForMountAsync(
        IVolumeProbe probe, string volumeGuid, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe.TryGetByGuid(volumeGuid) is { MountPoints.Count: > 0 } probed)
                return probed;
            await Task.Delay(500, ct);
        }
        return null;
    }

    /// <summary>
    /// Mirrors the live probe into the catalog row — the same thing <c>VolumeSyncService</c> does
    /// on a device event. The harness does not invent the offline state here: it reports what the
    /// hardware actually says.
    /// </summary>
    private static async Task SyncOnlineStateFromProbeAsync(ScenarioContext ctx, IVolumeProbe probe, string volumeGuid)
    {
        var probed = probe.TryGetByGuid(volumeGuid);
        var online = probed is { MountPoints.Count: > 0 };

        await ctx.Env.WithDbAsync(async db =>
        {
            var volume = await db.Volumes.FirstAsync(v => v.VolumeGuid == volumeGuid, ctx.Ct);
            volume.IsOnline = online;
            volume.LastSeenUtc = DateTime.UtcNow;
            if (probed is not null && online)
                volume.FreeBytesLastKnown = probed.FreeBytes;
            await db.SaveChangesAsync(ctx.Ct);
        });
    }
}
