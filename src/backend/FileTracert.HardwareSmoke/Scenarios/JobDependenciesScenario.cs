using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data.Entities;
using FileTracert.HardwareSmoke.Harness;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Step 9c on real hardware: what happens when two queued operations want the same thing.
///
/// The promise being tested is the one the queue could not keep before: an operation is never
/// refused because another one is in the way (§4), it waits — and while it waits it owns
/// nothing, so the projection keeps showing the FIRST job's promise. Then the queue runs, in
/// dependency order, and the disk agrees.
///
/// The second round is the other half of §5: cancelling a prerequisite must NOT cascade. The
/// dependent stays in the queue, parked on a reason that says what happened.
/// </summary>
public sealed class JobDependenciesScenario : Scenario
{
    private const string NewFolder = "album-dip";
    private const string MovedFile = @"dip\panorama.jpg";
    private const string RenamedFile = @"dip\bozza.jpg";

    public override string Name => "job-dependencies";

    public override string Description =>
        "A second operation on the same entity is queued Blocked(DependencyPending), not refused; "
        + "the queue then runs in dependency order, and cancelling a prerequisite never cascades.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        ctx.Source.CreateFile(MovedFile, 64 * 1024);
        ctx.Source.CreateFile(RenamedFile, 12 * 1024);
        await ctx.IndexSourceAsync(AllowEverything());

        var moved = await AssertCatalogHasFileAsync(
            ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(MovedFile), "arrange");
        var renamed = await AssertCatalogHasFileAsync(
            ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(RenamedFile), "arrange");
        if (moved is null || renamed is null) return;

        var folderPath = ctx.Target.RelativePath(NewFolder);
        var folderFullPath = ctx.Target.FullPath(NewFolder);

        // ── act 1: a folder, a file into it, then a SECOND op on that file ────
        var createJob = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder,
            TargetVolumeId = ctx.TargetVolumeId,
            TargetRelativePath = folderPath,
        }, ctx.Ct);

        // Legal by §5 even though the folder exists only in the queue, and NOT a conflict:
        // two destinations that merely nest are not the same destination.
        var moveJob = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = moved.Id,
            TargetVolumeId = ctx.TargetVolumeId,
            TargetRelativePath = folderPath,
        }, ctx.Ct);

        // The one that used to be a 409: same entity, already spoken for.
        var secondOnSameFile = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = moved.Id,
            NewName = "panorama-2.jpg",
        }, ctx.Ct);

        // ── assert BEFORE execution ───────────────────────────────────────────
        ctx.Assert.Equal(JobState.Pending, ToState(createJob.State), "the CreateFolder job at enqueue");
        ctx.Assert.Equal(JobState.Pending, ToState(moveJob.State),
            "a move INTO a folder that only exists in the queue must not be blocked (§5)");

        ctx.Assert.Equal(JobState.Blocked, ToState(secondOnSameFile.State),
            "the second operation on one entity is queued, not refused (§4)");
        ctx.Assert.Equal(nameof(JobBlockReason.DependencyPending), secondOnSameFile.BlockReason,
            "block reason of the second operation");
        ctx.Assert.Equal(moveJob.Id, secondOnSameFile.DependsOnJobId ?? -1,
            "the dependency must name the job that holds the entity");

        var beforeRun = await ReloadFileAsync(ctx, moved.Id);
        ctx.Assert.Equal(moveJob.Id, beforeRun.PendingJobId ?? -1,
            "the overlay still belongs to the FIRST job — a blocked dependent owns nothing");
        ctx.Assert.Equal(EntityPendingState.PendingMove, beforeRun.PendingState,
            "the projected state must be the first job's, not the blocked one's");
        ctx.Assert.True(beforeRun.PendingName is null,
            "the blocked rename must not have written its new name into the projection");

        // ── act 2: let the real queue run it all ──────────────────────────────
        await ctx.Queue.StartWorkerAsync(ctx.Ct);
        var createDone = await ctx.Queue.WaitForTerminalAsync(createJob.Id, ctx.Timeout, ctx.Ct);
        var moveDone = await ctx.Queue.WaitForTerminalAsync(moveJob.Id, ctx.Timeout, ctx.Ct);

        // The dependent is released by the revaluation the worker runs after each completion;
        // it has to get there on its own, without anybody retrying it.
        var renameDone = await ctx.Queue.WaitForTerminalAsync(secondOnSameFile.Id, ctx.Timeout, ctx.Ct);

        ctx.Assert.Equal(JobState.Completed, createDone.State,
            $"CreateFolder job ({QueueDriver.Describe(createDone)})");
        ctx.Assert.Equal(JobState.Completed, moveDone.State,
            $"MoveFile job ({QueueDriver.Describe(moveDone)})");
        ctx.Assert.Equal(JobState.Completed, renameDone.State,
            $"the released dependent ({QueueDriver.Describe(renameDone)})");

        // Dependency order, observed rather than assumed: the dependent cannot have finished
        // before the job it was waiting for.
        ctx.Assert.True(renameDone.CompletedUtc >= moveDone.CompletedUtc,
            $"the dependent completed at {renameDone.CompletedUtc:O}, before its prerequisite " +
            $"at {moveDone.CompletedUtc:O}");

        // ── assert AFTER: the disk agrees, and the snapshot was refreshed ─────
        ctx.Assert.DirectoryExists(folderFullPath, "the queued folder after its job ran");
        ctx.Assert.FileExists(Path.Combine(folderFullPath, "panorama-2.jpg"),
            "the file must carry BOTH operations: moved by the first, renamed by the second. "
            + "The rename was queued against the old location, so this is the snapshot refresh "
            + "on real disks (finding 8a).");
        ctx.Assert.FileMissing(ctx.Source.FullPath(MovedFile), "the file at its original path");
        AssertNoPartialsAnywhere(ctx);

        var afterRun = await ReloadFileAsync(ctx, moved.Id);
        ctx.Assert.Equal("panorama-2.jpg", afterRun.Name, "the physical name after both jobs");
        ctx.Assert.Equal(EntityPendingState.None, afterRun.PendingState,
            "no overlay may survive the last job's completion");

        // ── act 3: cancel a prerequisite, and check nothing cascades ──────────
        await ctx.Queue.StopWorkerAsync();

        var prerequisite = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = renamed.Id,
            NewName = "bozza-1.jpg",
        }, ctx.Ct);

        var dependent = await ctx.Queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = renamed.Id,
            NewName = "bozza-2.jpg",
        }, ctx.Ct);

        ctx.Assert.Equal(prerequisite.Id, dependent.DependsOnJobId ?? -1,
            "the second rename must wait for the first");

        await ctx.Queue.CancelAsync(prerequisite.Id, ctx.Ct);

        var cancelled = await ctx.Queue.LoadJobAsync(prerequisite.Id, ctx.Ct);
        var parked = await ctx.Queue.LoadJobAsync(dependent.Id, ctx.Ct);

        ctx.Assert.Equal(JobState.Cancelled, cancelled.State, "the cancelled prerequisite");
        ctx.Assert.Equal(JobState.Blocked, parked.State,
            $"§5: no cascade of cancellations ({QueueDriver.Describe(parked)})");
        ctx.Assert.Equal(JobBlockReason.DependencyCancelled, parked.BlockReason,
            "the dependent's block reason after its prerequisite was cancelled");
        ctx.Assert.FileExists(ctx.Source.FullPath(RenamedFile),
            "neither job ran, so the file must still be there under its original name");
    }

    private static JobState ToState(string value) => Enum.Parse<JobState>(value);

    private static Task<FileEntry> ReloadFileAsync(ScenarioContext ctx, int fileId) =>
        ctx.Env.WithDbAsync(db => db.Files.AsNoTracking().FirstAsync(f => f.Id == fileId, ctx.Ct));
}
