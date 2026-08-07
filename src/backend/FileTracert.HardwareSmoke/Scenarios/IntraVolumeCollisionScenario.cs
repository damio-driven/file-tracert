using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// WP1 (C20) — intra-volume move onto an occupied destination. The typed collision must park the
/// job as <c>Blocked(NameCollision)</c> (reactivatable, §4), never terminal Failed, and neither
/// file may be touched.
/// </summary>
public sealed class IntraVolumeCollisionScenario : Scenario
{
    public override string Name => "intra-collision-blocked";

    public override string Description =>
        "Intra-volume move onto an existing file: Blocked(NameCollision), both files untouched.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: mover and occupant inside the source area (same volume) ──
        var movingAbsolute = ctx.Source.CreateFile(@"from\same.bin", 8 * 1024);
        var occupantAbsolute = ctx.Source.CreateFile(@"to\same.bin", 4 * 1024);
        var movingHash = ScenarioAssertions.Sha256(movingAbsolute);
        var occupantHash = ScenarioAssertions.Sha256(occupantAbsolute);

        await ctx.IndexSourceAsync(AllowEverything());
        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(@"from\same.bin"));
        if (fileRow is null)
        {
            ctx.Assert.Fail("arrange failed: no catalog row for the moving file.");
            return;
        }

        // ── act ───────────────────────────────────────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = fileRow.Id,
            TargetVolumeId = ctx.SourceVolumeId,
            TargetRelativePath = ctx.Source.RelativePath("to"),
        }, ctx.Ct);

        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitAsync(job.Id,
            j => j.State is JobState.Blocked or JobState.Failed or JobState.Completed,
            ctx.Timeout, "the job to settle", ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Blocked, finished.State,
            $"a name collision must park, not fail ({Harness.QueueDriver.Describe(finished)})");
        ctx.Assert.Equal(JobBlockReason.NameCollision, finished.BlockReason,
            "block reason of the collided move");

        ctx.Assert.Equal(movingHash, ScenarioAssertions.Sha256(movingAbsolute), "moving file untouched");
        ctx.Assert.Equal(occupantHash, ScenarioAssertions.Sha256(occupantAbsolute), "occupant untouched");
    }
}
