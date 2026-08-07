using FileTracert.Contracts.Enums;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// WP3 C21 — a folder move whose expansion yields no items (empty folder, or every file filtered
/// out). The state machine must not walk Pending → Completed without a single syscall: either it
/// really creates the destination folder, or it reports an honest non-success. What it may never
/// do is claim success while the destination was never created.
/// </summary>
public sealed class MoveFolderNothingToCopyScenario : Scenario
{
    public override string Name => "move-folder-nothing-to-copy";

    public override string Description =>
        "MoveFolder across volumes of an all-excluded folder: honest outcome, never Completed without a syscall.";

    public override PairRequirement Requires => PairRequirement.Cross;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange: a folder whose only content the filter rejects ───────────
        const string readme = @"album\readme.txt";
        ctx.Source.CreateFile(readme, 2 * 1024);
        ctx.Source.CreateDirectory(@"album\empty-sub");

        var index = await ctx.IndexSourceAsync(AllowOnly("jpg"));
        ctx.Assert.Equal(0, index.IndexedFiles.Count, "indexed file count (the filter rejects everything)");

        var albumRow = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath("album"));
        if (albumRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for directory '{ctx.Source.RelativePath("album")}'.");
            return;
        }

        // ── act ───────────────────────────────────────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(MoveFolderTo(ctx, albumRow.Id), ctx.Ct);
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        // ── assert: the outcome must match what physically happened ───────────
        var targetFolder = Path.Combine(ctx.Target.RootFullPath, "album");

        if (finished.State == JobState.Completed)
        {
            ctx.Assert.DirectoryExists(
                targetFolder,
                "job reported Completed, so the destination folder must exist " +
                "(a Completed job that made no syscall is a lie)");
        }
        else
        {
            ctx.Assert.True(
                finished.State == JobState.Blocked,
                $"a folder move with nothing to copy must be Completed-with-folder or Blocked, " +
                $"not {Harness.QueueDriver.Describe(finished)}");
        }

        // ── assert: whatever the verdict, uncopied content is untouched ───────
        ctx.Assert.FileExists(ctx.Source.FullPath(readme),
            "the filtered-out file was never copied and must survive on the source");
        ctx.Assert.DirectoryExists(ctx.Source.FullPath("album"),
            "the source folder still holds uncopied content and must not be recycled");

        AssertNoPartialsAnywhere(ctx);

        ctx.Log($"outcome: {Harness.QueueDriver.Describe(finished)}; target folder exists: {Directory.Exists(targetFolder)}");
    }
}
