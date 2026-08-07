using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Search;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// RenameFolder is metadata-only and instant, but it has to cascade: the directory subtree keeps
/// its identity under a new path, and the FTS index must find the files at the new path (a rename
/// does not change file names, so only the path column moves).
/// </summary>
public sealed class RenameFolderScenario : Scenario
{
    private const string NewName = "album-renamed";

    public override string Name => "rename-folder";

    public override string Description =>
        "RenameFolder: applied on disk, directory subtree and FTS paths cascaded in the catalog.";

    public override PairRequirement Requires => PairRequirement.Intra;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        ctx.Source.CreateFile(@"album\photo.jpg", 16 * 1024);
        ctx.Source.CreateFile(@"album\sub\deep.jpg", 16 * 1024);
        await ctx.IndexSourceAsync(AllowEverything());

        var albumRow = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath("album"));
        if (albumRow is null)
        {
            ctx.Assert.Fail($"arrange failed: no catalog row for '{ctx.Source.RelativePath("album")}'.");
            return;
        }

        // ── act ───────────────────────────────────────────────────────────────
        var job = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder,
            SourceDirectoryId = albumRow.Id,
            NewName = NewName,
        }, ctx.Ct);

        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        // ── assert (filesystem) ───────────────────────────────────────────────
        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state ({Harness.QueueDriver.Describe(finished)})");

        ctx.Assert.DirectoryMissing(ctx.Source.FullPath("album"), "old folder after the rename");
        ctx.Assert.FileExists(ctx.Source.FullPath($@"{NewName}\photo.jpg"), "file under the renamed folder");
        ctx.Assert.FileExists(ctx.Source.FullPath($@"{NewName}\sub\deep.jpg"), "nested file under the renamed folder");

        // ── assert (catalog: the subtree moved, keeping its identity) ─────────
        var renamedPath = ctx.Source.RelativePath(NewName);
        var renamedRow = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, renamedPath);
        ctx.Assert.True(renamedRow is not null, $"directory row cascaded to '{renamedPath}'");
        if (renamedRow is not null)
            ctx.Assert.Equal(albumRow.Id, renamedRow.Id, "directory row identity preserved across the rename");

        var subRow = await FindDirectoryRowAsync(ctx, ctx.SourceVolumeId, ScanPath.Join(renamedPath, "sub"));
        ctx.Assert.True(subRow is not null, $"nested directory row cascaded to '{ScanPath.Join(renamedPath, "sub")}'");

        var fileRow = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ScanPath.Join(renamedPath, "photo.jpg"));
        ctx.Assert.True(fileRow is not null, "file row reachable under the renamed folder path");

        // ── assert (FTS: the new path is searchable) ──────────────────────────
        var hits = await ctx.Env.WithScopeAsync(sp => sp.GetRequiredService<IFileSearchIndex>().SearchAsync(
            new FileSearchQuery(
                Text: "renamed", Scope: SearchScope.FullPath, Category: null, Extensions: null,
                SizeBytesMin: null, SizeBytesMax: null, ModifiedFrom: null, ModifiedTo: null,
                VolumeId: null, OnlineOnly: false, Sort: SearchSort.Relevance, Desc: false,
                Skip: 0, Take: 50),
            ctx.Ct));

        ctx.Assert.True(
            fileRow is not null && hits.Items.Contains(fileRow.Id),
            $"the FTS index must find the file under its new path (search returned {hits.Items.Count} hit(s))");
    }
}

/// <summary>
/// CreateFolder is a plain mkdir at execution time, but it must also land in the catalog so the
/// folder is navigable and later operations can target it.
/// </summary>
public sealed class CreateFolderScenario : Scenario
{
    private const string FolderName = "brand-new";

    public override string Name => "create-folder";

    public override string Description => "CreateFolder: directory created on the target volume and present in the catalog.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        var relativePath = ctx.Target.RelativePath(FolderName);
        var absolutePath = Path.Combine(ctx.Target.RootFullPath, FolderName);

        var job = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder,
            TargetVolumeId = ctx.TargetVolumeId,
            TargetRelativePath = relativePath,
        }, ctx.Ct);

        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var finished = await ctx.Queue.WaitForTerminalAsync(job.Id, ctx.Timeout, ctx.Ct);

        ctx.Assert.Equal(JobState.Completed, finished.State,
            $"job state ({Harness.QueueDriver.Describe(finished)})");

        ctx.Assert.DirectoryExists(absolutePath, "folder created on disk");

        var row = await FindDirectoryRowAsync(ctx, ctx.TargetVolumeId, relativePath);
        ctx.Assert.True(row is not null, $"catalog row for the new folder '{relativePath}'");
        if (row is not null)
            ctx.Assert.True(row.IsMaterialized, "the created folder must be materialized in the catalog");
    }
}
