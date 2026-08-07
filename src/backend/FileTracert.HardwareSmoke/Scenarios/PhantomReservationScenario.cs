using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// WP1 (finding #5) — phantom reservation after a crash on a terminal state: a Completed job
/// left its ledger entries IsActive (the release used to run in a separate transaction). The
/// worker's startup rebuild must reconcile them away, so a new job that physically fits is
/// executed instead of being starved by space a finished job no longer claims.
/// </summary>
public sealed class PhantomReservationScenario : Scenario
{
    public override string Name => "phantom-reservation-rebuild";

    public override string Description =>
        "Ledger entries orphaned by a crash on a terminal job are reconciled at startup; feasibility recovers.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        const long fileSize = 256 * 1024;
        const long phantomSize = 64L * 1024 * 1024;

        // ── arrange: the crash footprint — terminal job, reservation still active ──
        await ctx.Env.WithDbAsync(async db =>
        {
            var phantom = new OperationJob
            {
                Type = JobType.MoveFile, State = JobState.Completed,
                IsIntraVolume = false,
                SourceVolumeId = ctx.SourceVolumeId, TargetVolumeId = ctx.TargetVolumeId,
                TargetRelativePath = @"phantom\gone.bin",
                TotalBytes = phantomSize, RequiredBytesTarget = phantomSize,
                SequenceOrder = 1,
                StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
            };
            db.OperationJobs.Add(phantom);
            db.SpaceLedgerEntries.Add(new SpaceLedgerEntry
            {
                Job = phantom, VolumeId = ctx.TargetVolumeId,
                DeltaBytes = phantomSize, IsActive = true,
            });
            await db.SaveChangesAsync(ctx.Ct);
        });
        ctx.Log($"forged phantom: Completed job with an active {phantomSize / (1024 * 1024)} MB reservation");

        // A real move that fits the disk — but NOT if the phantom were still counted.
        const string relative = @"docs\real.bin";
        var sourceAbsolute = ctx.Source.CreateFile(relative, fileSize);
        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);
        // Free space such that the real file fits with room, the phantom does not.
        await SetVolumeFreeBytesAsync(ctx, ctx.TargetVolumeId, fileSize * 4);

        // ── act: worker start runs RebuildFromDbAsync → reconciliation ────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"the real job must run despite the phantom ({Harness.QueueDriver.Describe(finished)})");

        var activePhantoms = await ctx.Env.WithDbAsync(db =>
            db.SpaceLedgerEntries.CountAsync(
                e => e.IsActive && e.Job.State == JobState.Completed, ctx.Ct));
        ctx.Assert.Equal(0, activePhantoms, "active ledger entries on terminal jobs after the rebuild");

        AssertNoPartialsAnywhere(ctx);
    }
}
