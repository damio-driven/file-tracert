using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// WP3 C22 — a folder move that cannot mean anything must be refused where the user can still see
/// the message: at enqueue (the API maps <see cref="ArgumentException"/>/<see cref="InvalidOperationException"/>
/// to 400), not hours later as a Failed job in the queue. Two impossible requests:
///   • moving a folder INTO ITSELF (or into one of its own descendants);
///   • moving a folder into the parent it is already in (a no-op).
/// Each uses its own source folder so the one-pending-op-per-entity guard cannot mask the verdict.
/// </summary>
public sealed class MoveFolderRejectedAtEnqueueScenario : Scenario
{
    public override string Name => "move-folder-rejected-at-enqueue";

    public override string Description =>
        "MoveFolder into itself / into its current parent: refused at enqueue (400), never enqueued as a job.";

    public override PairRequirement Requires => PairRequirement.Intra;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: two independent folders, each with one indexed file ──────
        ctx.Source.CreateFile(@"self\inner\photo.jpg", 8 * 1024);
        ctx.Source.CreateFile(@"noop\photo.jpg", 8 * 1024);
        await ctx.IndexSourceAsync(AllowEverything());

        var selfRow = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath("self"));
        var noopRow = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath("noop"));

        if (selfRow is null || noopRow is null)
        {
            ctx.Assert.Fail("arrange failed: the fixture folders were not indexed.");
            return;
        }

        // ── act + assert: move 'self' into 'self\inner' ───────────────────────
        var intoSelf = await ctx.Queue.TryEnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = selfRow.Id,
            TargetVolumeId = ctx.SourceVolumeId,
            TargetRelativePath = ctx.Source.RelativePath(@"self\inner"),
        }, ctx.Ct);

        AssertRejected(ctx, intoSelf, "moving a folder into its own descendant");

        // ── act + assert: move 'noop' into the parent it already lives in ─────
        var intoParent = await ctx.Queue.TryEnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = noopRow.Id,
            TargetVolumeId = ctx.SourceVolumeId,
            TargetRelativePath = ctx.Source.RootRelativePath,
        }, ctx.Ct);

        AssertRejected(ctx, intoParent, "moving a folder into the parent it is already in (no-op)");

        // ── assert: nothing reached the queue ─────────────────────────────────
        var jobCount = await ctx.Env.WithDbAsync(db => db.OperationJobs.CountAsync(ctx.Ct));
        ctx.Assert.Equal(0, jobCount, "jobs created by the two rejected requests");

        // ── assert: the fixtures are exactly where they were ──────────────────
        ctx.Assert.FileExists(ctx.Source.FullPath(@"self\inner\photo.jpg"), "untouched fixture after a rejected enqueue");
        ctx.Assert.FileExists(ctx.Source.FullPath(@"noop\photo.jpg"), "untouched fixture after a rejected enqueue");
    }

    private static void AssertRejected(ScenarioContext ctx, Exception? thrown, string what)
    {
        if (thrown is null)
        {
            ctx.Assert.Fail($"{what}: the request was accepted; it must be refused at enqueue (400).");
            return;
        }

        ctx.Assert.True(
            thrown is ArgumentException or InvalidOperationException,
            $"{what}: refused with {thrown.GetType().Name} ('{thrown.Message}'), " +
            "but only ArgumentException/InvalidOperationException map to a 400 the user can read.");

        ctx.Log($"{what} → {thrown.GetType().Name}: {thrown.Message}");
    }
}
