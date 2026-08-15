using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Step 9b end to end on real hardware: queuing an operation must mutate the PROJECTION at once
/// (§5), executing it must replace the projection with the physical fact, and a re-scan must
/// leave both alone.
///
/// The centre of it is the §5 promise that has no other coverage on real disks: a folder that
/// exists only in the queue is a legal destination. The scenario queues a CreateFolder and then,
/// before anything has touched the disk, queues a file INTO it — then lets the real queue run
/// and checks the disk agrees.
/// </summary>
public sealed class ProjectionOverlayScenario : Scenario
{
    private const string NewFolder = "album-2026";
    private const string MovedFile = @"shots\sunset.jpg";
    private const string RenamedFile = @"shots\draft.jpg";
    private const string RenamedTo = "tramonto.jpg";

    public override string Name => "projection-overlay";

    public override string Description =>
        "Queuing mutates the projection at once (a queued folder is a legal destination); executing clears it; a re-scan keeps both.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        ctx.Source.CreateFile(MovedFile, 48 * 1024);
        ctx.Source.CreateFile(RenamedFile, 16 * 1024);
        await ctx.IndexSourceAsync(AllowEverything());

        var moved = await AssertCatalogHasFileAsync(
            ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(MovedFile), "arrange");
        var renamed = await AssertCatalogHasFileAsync(
            ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(RenamedFile), "arrange");
        if (moved is null || renamed is null) return;

        var folderPath = ctx.Target.RelativePath(NewFolder);
        var folderFullPath = ctx.Target.FullPath(NewFolder);

        // ── act 1: queue a folder, then queue a file INTO it ──────────────────
        var createJob = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder,
            TargetVolumeId = ctx.TargetVolumeId,
            TargetRelativePath = folderPath,
        }, ctx.Ct);

        // This is the §5 case: the destination exists only in the projection. If the enqueue
        // validated against the disk instead, this call would be the one that fails.
        var moveJob = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = moved.Id,
            TargetVolumeId = ctx.TargetVolumeId,
            TargetRelativePath = folderPath,
        }, ctx.Ct);

        var renameJob = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = renamed.Id,
            NewName = RenamedTo,
        }, ctx.Ct);

        // ── assert BEFORE execution: the projection moved, the disk did not ───
        ctx.Assert.DirectoryMissing(folderFullPath, "the queued folder before the job ran");
        ctx.Assert.FileExists(ctx.Source.FullPath(MovedFile), "the file still at its source before the job ran");

        var folderRow = await AssertCatalogHasDirectoryAsync(
            ctx, ctx.TargetVolumeId, folderPath, "the queued folder in the projection");
        if (folderRow is null) return;

        ctx.Assert.Equal(EntityPendingState.PendingCreate, folderRow.PendingState,
            "PendingState of the queued folder");
        ctx.Assert.Equal(createJob.Id, folderRow.PendingJobId ?? -1, "PendingJobId of the queued folder");
        ctx.Assert.True(!folderRow.IsMaterialized,
            "a folder that only exists in the queue must not claim to be on disk");

        var movedPending = await ReloadFileAsync(ctx, moved.Id);
        ctx.Assert.Equal(folderRow.Id, movedPending.PendingDirectoryId ?? -1,
            "the queued move must point the file at the queued folder");
        ctx.Assert.Equal(EntityPendingState.PendingMove, movedPending.PendingState,
            "PendingState of the moved file");
        ctx.Assert.Equal(moveJob.Id, movedPending.PendingJobId ?? -1, "PendingJobId of the moved file");
        ctx.Assert.Equal(moved.DirectoryId, movedPending.DirectoryId,
            "the physical directory must not change before the job runs");

        var renamePending = await ReloadFileAsync(ctx, renamed.Id);
        ctx.Assert.Equal(RenamedTo, renamePending.PendingName ?? "(null)", "PendingName of the renamed file");
        ctx.Assert.Equal("draft.jpg", renamePending.Name, "the physical name must not change before the job runs");

        // The FTS index carries the PROJECTED name: the new name answers, the old one does not.
        var newNameHits = await SearchByNameAsync(ctx, "tramonto");
        ctx.Assert.True(newNameHits.Contains(renamed.Id),
            $"the queued rename must be searchable under its new name; hits [{string.Join(", ", newNameHits)}], expected id {renamed.Id}");
        var oldNameHits = await SearchByNameAsync(ctx, "draft");
        ctx.Assert.True(!oldNameHits.Contains(renamed.Id),
            $"the old name must stop answering; hits [{string.Join(", ", oldNameHits)}]");

        // ── act 2: let the real queue execute everything ──────────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var createDone = await ctx.Queue.WaitForTerminalAsync(createJob.Id, ctx.Timeout, ctx.Ct);
        var moveDone = await ctx.Queue.WaitForTerminalAsync(moveJob.Id, ctx.Timeout, ctx.Ct);
        var renameDone = await ctx.Queue.WaitForTerminalAsync(renameJob.Id, ctx.Timeout, ctx.Ct);

        ctx.Assert.Equal(JobState.Completed, createDone.State,
            $"CreateFolder job ({Harness.QueueDriver.Describe(createDone)})");
        ctx.Assert.Equal(JobState.Completed, moveDone.State,
            $"MoveFile job ({Harness.QueueDriver.Describe(moveDone)})");
        ctx.Assert.Equal(JobState.Completed, renameDone.State,
            $"RenameFile job ({Harness.QueueDriver.Describe(renameDone)})");

        // ── assert AFTER: disk agrees, overlay gone, no duplicate rows ────────
        ctx.Assert.DirectoryExists(folderFullPath, "the folder after its job ran");
        ctx.Assert.FileExists(Path.Combine(folderFullPath, "sunset.jpg"), "the moved file at its destination");
        ctx.Assert.FileMissing(ctx.Source.FullPath(MovedFile), "the moved file at its old location");
        ctx.Assert.FileExists(ctx.Source.FullPath($@"shots\{RenamedTo}"), "the renamed file on disk");
        AssertNoPartialsAnywhere(ctx);

        var folderRows = await ctx.Env.WithDbAsync(db => db.Directories.AsNoTracking()
            .Where(d => d.VolumeId == ctx.TargetVolumeId && d.MaterializedPath == folderPath)
            .ToListAsync(ctx.Ct));
        ctx.Assert.Equal(1, folderRows.Count,
            "the completion must reuse the projected folder row, not add a second one beside it");
        if (folderRows.Count == 1)
        {
            ctx.Assert.True(folderRows[0].IsMaterialized, "the created folder must end up materialized");
            ctx.Assert.True(folderRows[0].IsPresent, "the created folder must end up present");
            ctx.Assert.Equal(EntityPendingState.None, folderRows[0].PendingState,
                "the folder overlay after completion");
            ctx.Assert.Equal(folderRow.Id, folderRows[0].Id, "the folder row identity across the completion");
        }

        var movedDone = await ReloadFileAsync(ctx, moved.Id);
        ctx.Assert.Equal(folderRow.Id, movedDone.DirectoryId, "the moved file's physical directory after completion");
        ctx.Assert.Equal(EntityPendingState.None, movedDone.PendingState, "the moved file's overlay after completion");
        ctx.Assert.True(movedDone.PendingDirectoryId is null, "PendingDirectoryId must be cleared on completion");

        var renameDoneRow = await ReloadFileAsync(ctx, renamed.Id);
        ctx.Assert.Equal(RenamedTo, renameDoneRow.Name, "the renamed file's physical name after completion");
        ctx.Assert.Equal(EntityPendingState.None, renameDoneRow.PendingState, "the renamed file's overlay after completion");
        ctx.Assert.True(renameDoneRow.PendingName is null, "PendingName must be cleared on completion");

        // ── act 3: re-scan and re-assert (closes the loop with step 9a) ───────
        await ctx.Queue.StopWorkerAsync();
        await EnsureWatchedRootAsync(ctx, ctx.Source, ctx.SourceVolumeId);
        await EnsureWatchedRootAsync(ctx, ctx.Target, ctx.TargetVolumeId);
        await ScanVolumeAsync(ctx, ctx.SourceVolumeId);
        if (ctx.TargetVolumeId != ctx.SourceVolumeId)
        {
            await ScanVolumeAsync(ctx, ctx.TargetVolumeId);
        }

        var movedRescanned = await ReloadFileAsync(ctx, moved.Id);
        ctx.Assert.Equal(folderRow.Id, movedRescanned.DirectoryId,
            "the re-scan must recognize the moved file, not create a second row for it");
        ctx.Assert.Equal(EntityPendingState.None, movedRescanned.PendingState,
            "a re-scan must never resurrect a cleared overlay");
        ctx.Assert.True(movedRescanned.IsPresent, "the moved file must still be present after the re-scan");

        var renameRescanned = await ReloadFileAsync(ctx, renamed.Id);
        ctx.Assert.Equal(RenamedTo, renameRescanned.Name, "the renamed file's identity across the re-scan");
        ctx.Assert.True(renameRescanned.IsPresent, "the renamed file must still be present after the re-scan");
    }

    private static Task<FileEntry> ReloadFileAsync(ScenarioContext ctx, int fileId) =>
        ctx.Env.WithDbAsync(db => db.Files.AsNoTracking().FirstAsync(f => f.Id == fileId, ctx.Ct));
}
