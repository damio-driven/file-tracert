using FileTracert.Contracts.Enums;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// WP3 #1 — the silent data-loss case. A folder catalogued with an "images only" filter also holds
/// sidecars and notes the catalog never saw. The move must carry the indexed files over and leave
/// everything it did NOT copy exactly where it is: a folder move may never destroy content that
/// exists nowhere else.
/// </summary>
public sealed class MoveFolderExcludedFilesScenario : Scenario
{
    public override string Name => "move-folder-excluded-files";

    public override string Description =>
        "MoveFolder across volumes with filtered-out files: only copied+verified files leave the source.";

    public override PairRequirement Requires => PairRequirement.Cross;

    private static readonly string[] IndexedFiles =
    [
        @"album\photo-1.jpg",
        @"album\photo-2.jpg",
        @"album\sub\photo-3.jpg",
    ];

    private static readonly string[] ExcludedFiles =
    [
        @"album\photo-1.jpg.xmp",
        @"album\notes.txt",
        @"album\sub\sub-notes.txt",
        @"album\only-text\readme.txt",
    ];

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in IndexedFiles)
            hashes[relative] = ScenarioAssertions.Sha256(ctx.Source.CreateFile(relative, 64 * 1024));

        foreach (var relative in ExcludedFiles)
            ctx.Source.CreateFile(relative, 4 * 1024);

        var index = await ctx.IndexSourceAsync(AllowOnly("jpg"));
        ctx.Assert.Equal(IndexedFiles.Length, index.IndexedFiles.Count, "indexed file count");
        ctx.Assert.Equal(ExcludedFiles.Length, index.ExcludedFiles.Count, "filtered-out file count");

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

        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state ({Harness.QueueDriver.Describe(finished)})");

        // ── assert: the indexed files arrived intact and left the source ──────
        foreach (var relative in IndexedFiles)
        {
            var landed = Path.Combine(ctx.Target.RootFullPath, relative);
            ctx.Assert.FileExists(landed, $"copied file '{relative}' on the target");
            if (File.Exists(landed))
                ctx.Assert.Equal(hashes[relative], ScenarioAssertions.Sha256(landed), $"content of '{relative}'");

            ctx.Assert.FileMissing(ctx.Source.FullPath(relative), $"copied file '{relative}' on the source");
        }

        // ── assert: nothing that was never copied was destroyed ───────────────
        foreach (var relative in ExcludedFiles)
        {
            ctx.Assert.FileExists(
                ctx.Source.FullPath(relative),
                $"filtered-out file '{relative}' must stay on the source (it was never copied)");

            ctx.Assert.FileMissing(
                Path.Combine(ctx.Target.RootFullPath, relative),
                $"filtered-out file '{relative}' must NOT appear on the target");
        }

        // ── assert: directories that still hold uncopied content survive ──────
        foreach (var directory in new[] { "album", @"album\sub", @"album\only-text" })
        {
            ctx.Assert.DirectoryExists(
                ctx.Source.FullPath(directory),
                $"source directory '{directory}' still holds uncopied files and must not be recycled");
        }

        AssertNoPartialsAnywhere(ctx);

        // ── assert: the catalog follows the files ─────────────────────────────
        foreach (var relative in IndexedFiles)
        {
            var expectedPath = ctx.Target.RelativePath(relative);
            var row = await FindFileRowAsync(ctx, ctx.TargetVolumeId, expectedPath);
            ctx.Assert.True(row is not null, $"catalog row re-pointed to '{expectedPath}'");
        }

        ctx.Log($"{IndexedFiles.Length} indexed file(s) moved, {ExcludedFiles.Length} filtered-out file(s) left in place");
    }
}
