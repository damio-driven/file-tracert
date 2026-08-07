using FileTracert.Contracts.Enums;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// WP1 (finding #2) — cancel landing in the pre-delete window: the job is checkpointed at
/// <c>DeletingSource</c> with the copy finalized but the source still on disk. The user cancels
/// before the worker gets to the destructive step. The worker must honour the committed
/// Cancelled — never recycle the source of a cancelled job — and the landed target copy must be
/// reconciled into the catalog (fix #14).
/// </summary>
public sealed class CancelBeforeDeleteScenario : Scenario
{
    public override string Name => "cancel-before-delete";

    public override string Description =>
        "Cancel in the window before DeletingSource runs: source untouched, job stays Cancelled.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: enqueue + forge the pre-delete checkpoint ────────────────
        const string relative = @"docs\keep.bin";
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

        var targetAbsolute = Path.Combine(ctx.Target.RootFullPath, "keep.bin");
        File.Copy(sourceAbsolute, targetAbsolute); // finalized copy landed
        await ctx.Env.WithDbAsync(async db =>
        {
            var row = await db.OperationJobs.Include(j => j.Items).FirstAsync(j => j.Id == job.Id, ctx.Ct);
            row.State = JobState.DeletingSource;
            row.StartedUtc = DateTime.UtcNow;
            row.BytesProcessed = row.TotalBytes;
            var item = row.Items.Single();
            item.State = JobItemState.Verified;
            item.BytesCopied = item.SizeBytes;
            await db.SaveChangesAsync(ctx.Ct);
        });
        ctx.Log("forged checkpoint: copy finalized, source still present, job@DeletingSource");

        // ── act: the cancel wins the window, then the worker starts ───────────
        await ctx.Queue.CancelAsync(job.Id, ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        // Give the worker a real chance to (wrongly) pick the job before asserting.
        await Task.Delay(TimeSpan.FromSeconds(2), ctx.Ct);
        await ctx.Queue.StopWorkerAsync();

        // ── assert ────────────────────────────────────────────────────────────
        var finished = await ctx.Queue.LoadJobAsync(job.Id, ctx.Ct);
        ctx.Assert.Equal(JobState.Cancelled, finished.State,
            $"job state after cancel + worker restart ({Harness.QueueDriver.Describe(finished)})");

        ctx.Assert.FileExists(sourceAbsolute, "source of the cancelled job");
        if (File.Exists(sourceAbsolute))
            ctx.Assert.Equal(expectedHash, ScenarioAssertions.Sha256(sourceAbsolute), "source content untouched");
        AssertNoPartialsAnywhere(ctx);

        // Fix #14: the finalized copy on the target is real — it must be indexed, not a ghost.
        await AssertCatalogHasFileAsync(ctx, ctx.TargetVolumeId,
            ctx.Target.RelativePath("keep.bin"), "landed copy reconciled after cancel");
    }
}
