using FileTracert.Contracts.Enums;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Moves a single indexed file through the real queue and checks both halves of the promise:
/// the bytes arrived intact at the destination, and the source no longer holds them — plus the
/// catalog now points at the new location, with no <c>.fadit-partial</c> anywhere.
/// </summary>
public abstract class MoveFileScenarioBase : Scenario
{
    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        const string relative = @"album\holiday.jpg";
        var sourceAbsolute = ctx.Source.CreateFile(relative, 512 * 1024);
        var expectedHash = ScenarioAssertions.Sha256(sourceAbsolute);

        var index = await ctx.IndexSourceAsync(AllowEverything());
        ctx.Assert.Equal(1, index.IndexedFiles.Count, "indexed file count");

        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(relative));
        if (fileRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath(relative)}'.");
            return;
        }

        // ── act ───────────────────────────────────────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(MoveFileTo(ctx, fileRow.Id), ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        // ── assert (filesystem) ───────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state ({Harness.QueueDriver.Describe(finished)})");

        var targetAbsolute = Path.Combine(ctx.Target.RootFullPath, "holiday.jpg");
        ctx.Assert.FileExists(targetAbsolute, "moved file on the target");
        if (File.Exists(targetAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(targetAbsolute), "moved file content");

        ctx.Assert.FileMissing(sourceAbsolute, "source after the move");
        AssertNoPartialsAnywhere(ctx);

        // ── assert (catalog) ──────────────────────────────────────────────────
        var movedRow = await FindFileRowAsync(
            ctx, ctx.TargetVolumeId, ctx.Target.RelativePath("holiday.jpg"));
        ctx.Assert.True(movedRow is not null,
            $"catalog row re-pointed to '{ctx.Target.RelativePath("holiday.jpg")}' on volume {ctx.TargetVolumeId}");
        if (movedRow is not null)
            ctx.Assert.Equal(fileRow.Id, movedRow.Id, "catalog row identity preserved across the move");

        ctx.Log(Path.GetFileName(targetAbsolute) + $" moved; job bytes {finished.BytesProcessed}/{finished.TotalBytes}");
    }
}

/// <summary>Intra-volume move: a metadata-only rename, instant, no space reservation.</summary>
public sealed class MoveFileIntraVolumeScenario : MoveFileScenarioBase
{
    public override string Name => "move-file-intra";
    public override string Description => "MoveFile on one volume: instant, source gone, target present, catalog re-pointed.";
    public override PairRequirement Requires => PairRequirement.Intra;
}

/// <summary>
/// Cross-volume move: the full copy → verify → finalize → recycle pipeline. The source is sent to
/// the Recycle Bin, so a failed run is recoverable; the harness asserts it left its original path
/// and logs whether the volume actually has a bin.
/// </summary>
public sealed class MoveFileCrossVolumeScenario : MoveFileScenarioBase
{
    public override string Name => "move-file-cross";
    public override string Description => "MoveFile across volumes: copy→verify→finalize, source recycled, no partial left.";
    public override PairRequirement Requires => PairRequirement.Cross;
}
