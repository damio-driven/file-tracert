using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ExceptionExtensions;

namespace FileTracert.Tests.Business;

/// <summary>
/// State-machine tests for <see cref="JobExecutionEngine"/>. Uses a real in-memory
/// SQLite DB for persistence, NSubstitute for <see cref="IFileMover"/> and
/// <see cref="ISpaceLedger"/>, and a no-op FTS index so the IndexUpdater stays fast.
/// </summary>
public sealed class JobExecutionEngineTests : IDisposable
{
    private const int Vol1Id = 1;
    private const int Vol2Id = 2;
    private const int Dir1Id = 1;
    private const int File1Id = 1;
    private const string Vol1Guid = @"\\?\Volume{aaa-1}\";
    private const string Vol2Guid = @"\\?\Volume{bbb-2}\";

    private readonly SqliteInMemoryContext _harness;

    public JobExecutionEngineTests()
    {
        _harness = new SqliteInMemoryContext();
        using var setup = _harness.CreateContext();
        setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        SeedBase(setup);
    }

    public void Dispose() => _harness.Dispose();

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void SeedBase(FileTracertDbContext db)
    {
        db.Volumes.AddRange(
            new Volume
            {
                Id = Vol1Id, VolumeGuid = Vol1Guid,
                FileSystem = "NTFS", FreeBytesLastKnown = 100_000, IsOnline = true,
            },
            new Volume
            {
                Id = Vol2Id, VolumeGuid = Vol2Guid,
                FileSystem = "NTFS", FreeBytesLastKnown = 50_000, IsOnline = true,
            });

        db.Directories.Add(new DirectoryNode
        {
            Id = Dir1Id, VolumeId = Vol1Id, Name = "Docs",
            MaterializedPath = "Docs", IsMaterialized = true,
        });

        db.Files.Add(new FileEntry
        {
            Id = File1Id, VolumeId = Vol1Id, DirectoryId = Dir1Id,
            Name = "report.txt", Extension = "txt", Category = FileCategory.Document,
            SizeBytes = 1_000, IsPresent = true, IsIncluded = true,
            FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
            LastIndexedUtc = DateTime.UtcNow,
        });

        db.SaveChanges();
    }

    private int SeedJob(
        JobType type,
        JobState state,
        bool intraVolume,
        int? srcVol,
        int? tgtVol,
        string? tgtPath,
        long totalBytes,
        OperationJobItem[]? items = null)
    {
        using var db = _harness.CreateContext();

        var job = new OperationJob
        {
            Type = type,
            State = state,
            IsIntraVolume = intraVolume,
            SourceVolumeId = srcVol,
            TargetVolumeId = tgtVol,
            TargetRelativePath = tgtPath,
            TotalBytes = totalBytes,
            RequiredBytesTarget = totalBytes,
            SequenceOrder = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        if (items is not null)
            foreach (var item in items)
                job.Items.Add(item);

        db.OperationJobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    private JobExecutionEngine MakeEngine(IFileMover mover, bool feasible = true, TimeProvider? timeProvider = null)
    {
        var ledger = Substitute.For<ISpaceLedger>();
        var feasibilityResult = feasible
            ? new FeasibilityResult(1_000, 0, 50_000, 0, true, null, Feasible: true)
            : new FeasibilityResult(1_000, 0, 0, 1_000, true, Vol2Id, Feasible: false);
        ledger.ComputeFeasibilityAsync(default, default, default, default, default)
              .ReturnsForAnyArgs(Task.FromResult(feasibilityResult));
        ledger.ReleaseAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);

        var db = _harness.CreateContext();
        var fts = new FakeFileSearchIndex();
        var indexUpdater = new IndexUpdater(db, fts, NullLogger<IndexUpdater>.Instance);

        return new JobExecutionEngine(
            db, mover, ledger, indexUpdater, timeProvider ?? TimeProvider.System, NullLogger<JobExecutionEngine>.Instance);
    }

    private IFileMover DefaultMover()
    {
        var mover = Substitute.For<IFileMover>();
        mover.CopyFileAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Func<long, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        mover.Verify(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>())
             .Returns(true);
        return mover;
    }

    /// <summary>Manually-advanced clock for deterministic throttle testing (no real Task.Delay).</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }

    private async Task<JobState> ReadState(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs
            .Where(j => j.Id == jobId)
            .Select(j => j.State)
            .SingleAsync();
    }

    private async Task<JobBlockReason> ReadBlockReason(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs
            .Where(j => j.Id == jobId)
            .Select(j => j.BlockReason)
            .SingleAsync();
    }

    private static CancellationToken None => CancellationToken.None;

    // ── CreateFolder ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFolder_calls_mover_and_transitions_to_Completed()
    {
        var mover = DefaultMover();
        int jobId = SeedJob(
            JobType.CreateFolder, JobState.Pending, intraVolume: true,
            srcVol: null, tgtVol: Vol1Id, tgtPath: @"Docs\Archive", totalBytes: 0);

        await MakeEngine(mover).ExecuteJobAsync(jobId, None);

        (await ReadState(jobId)).Should().Be(JobState.Completed);
        mover.Received(1).CreateFolder(Vol1Guid, @"Docs\Archive");
    }

    // ── RenameFile ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameFile_calls_mover_and_transitions_to_Completed()
    {
        var mover = DefaultMover();
        int jobId = SeedJob(
            JobType.RenameFile, JobState.Pending, intraVolume: true,
            srcVol: Vol1Id, tgtVol: Vol1Id, tgtPath: "report_v2.txt", totalBytes: 0,
            items:
            [
                new OperationJobItem
                {
                    SourceRelativePath = @"Docs\report.txt",
                    TargetRelativePath = "report_v2.txt",
                    State = JobItemState.Pending,
                    SizeBytes = 0,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                },
            ]);

        await MakeEngine(mover).ExecuteJobAsync(jobId, None);

        (await ReadState(jobId)).Should().Be(JobState.Completed);
        mover.Received(1).RenameIntraVolume(Vol1Guid, @"Docs\report.txt", "report_v2.txt");
    }

    // ── MoveFile intra-volume ─────────────────────────────────────────────────

    [Fact]
    public async Task MoveFile_intra_volume_calls_mover_and_transitions_to_Completed()
    {
        var mover = DefaultMover();
        int jobId = SeedJob(
            JobType.MoveFile, JobState.Pending, intraVolume: true,
            srcVol: Vol1Id, tgtVol: Vol1Id, tgtPath: @"Docs\Sub\report.txt", totalBytes: 0,
            items:
            [
                new OperationJobItem
                {
                    SourceRelativePath = @"Docs\report.txt",
                    TargetRelativePath = @"Docs\Sub\report.txt",
                    State = JobItemState.Pending,
                    SizeBytes = 0,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                },
            ]);

        await MakeEngine(mover).ExecuteJobAsync(jobId, None);

        (await ReadState(jobId)).Should().Be(JobState.Completed);
        mover.Received(1).MoveIntraVolume(Vol1Guid, @"Docs\report.txt", @"Docs\Sub\report.txt");
    }

    // ── MoveFile cross-volume — happy path ────────────────────────────────────

    [Fact]
    public async Task MoveFile_cross_volume_runs_full_state_machine_to_Completed()
    {
        var mover = DefaultMover();
        int jobId = SeedJob(
            JobType.MoveFile, JobState.Pending, intraVolume: false,
            srcVol: Vol1Id, tgtVol: Vol2Id, tgtPath: @"Backup\report.txt", totalBytes: 1_000,
            items:
            [
                new OperationJobItem
                {
                    SourceRelativePath = @"Docs\report.txt",
                    TargetRelativePath = @"Backup\report.txt",
                    State = JobItemState.Pending,
                    SizeBytes = 1_000,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                },
            ]);

        await MakeEngine(mover, feasible: true).ExecuteJobAsync(jobId, None);

        (await ReadState(jobId)).Should().Be(JobState.Completed);

        await using var db = _harness.CreateContext();
        var item = await db.OperationJobItems.SingleAsync(i => i.JobId == jobId);
        item.State.Should().Be(JobItemState.Done);

#pragma warning disable CS4014
        mover.Received(1).CopyFileAsync(
            Vol1Guid, @"Docs\report.txt",
            Vol2Guid, @"Backup\report.txt.fadit-partial",
            Arg.Any<Func<long, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
#pragma warning restore CS4014
        mover.Received(1).Verify(
            Vol1Guid, @"Docs\report.txt",
            Vol2Guid, @"Backup\report.txt.fadit-partial",
            withHash: false);
        mover.Received(1).FinalizePartial(
            Vol2Guid, @"Backup\report.txt.fadit-partial", @"Backup\report.txt");
        mover.Received(1).DeleteToRecycleBin(Vol1Guid, @"Docs\report.txt");
    }

    // ── MoveFile cross-volume — progress persists mid-copy, not only at completion ────

    [Fact]
    public async Task MoveFile_cross_volume_persists_BytesProcessed_mid_copy_once_throttle_interval_elapses()
    {
        var timeProvider = new ManualTimeProvider();

        int jobId = SeedJob(
            JobType.MoveFile, JobState.Pending, intraVolume: false,
            srcVol: Vol1Id, tgtVol: Vol2Id, tgtPath: @"Backup\bigfile.bin", totalBytes: 1_000_000,
            items:
            [
                new OperationJobItem
                {
                    SourceRelativePath = @"Docs\bigfile.bin",
                    TargetRelativePath = @"Backup\bigfile.bin",
                    State = JobItemState.Pending,
                    SizeBytes = 1_000_000,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                },
            ]);

        long? bytesProcessedMidCopy = null;

        async Task RunProgressTicksAsync(CallInfo callInfo)
        {
            var onProgress = callInfo.ArgAt<Func<long, CancellationToken, Task>>(4);
            var ct = callInfo.ArgAt<CancellationToken>(5);

            await onProgress(400_000, ct);                        // 0.0s elapsed — must NOT persist yet
            timeProvider.Advance(TimeSpan.FromMilliseconds(500));
            await onProgress(600_000, ct);                        // 0.5s elapsed — must NOT persist yet
            timeProvider.Advance(TimeSpan.FromMilliseconds(600));  // 1.1s elapsed — past the throttle
            await onProgress(900_000, ct);                        // must persist here, before the item finishes

            // Read via a separate context: proves the value is actually committed to the DB at
            // this point in the copy, not just held on the tracked entity in memory.
            await using var probe = _harness.CreateContext();
            bytesProcessedMidCopy = await probe.OperationJobs
                .Where(j => j.Id == jobId)
                .Select(j => j.BytesProcessed)
                .SingleAsync();
        }

        var mover = Substitute.For<IFileMover>();
        mover.Verify(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
             .Returns(true);
        mover.CopyFileAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Func<long, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => RunProgressTicksAsync(callInfo));

        await MakeEngine(mover, feasible: true, timeProvider).ExecuteJobAsync(jobId, None);

        bytesProcessedMidCopy.Should().Be(900_000,
            "the 3rd tick crossed the 1s throttle and should have persisted mid-copy, before the item finished");
        (await ReadState(jobId)).Should().Be(JobState.Completed);
    }

    // ── MoveFile cross-volume — hard space check fails ────────────────────────

    [Fact]
    public async Task MoveFile_cross_volume_insufficient_space_blocks_job()
    {
        var mover = DefaultMover();
        int jobId = SeedJob(
            JobType.MoveFile, JobState.Pending, intraVolume: false,
            srcVol: Vol1Id, tgtVol: Vol2Id, tgtPath: @"Backup\report.txt", totalBytes: 1_000,
            items:
            [
                new OperationJobItem
                {
                    SourceRelativePath = @"Docs\report.txt",
                    TargetRelativePath = @"Backup\report.txt",
                    State = JobItemState.Pending,
                    SizeBytes = 1_000,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                },
            ]);

        await MakeEngine(mover, feasible: false).ExecuteJobAsync(jobId, None);

        (await ReadState(jobId)).Should().Be(JobState.Blocked);
        (await ReadBlockReason(jobId)).Should().Be(JobBlockReason.InsufficientSpace);
#pragma warning disable CS4014
        mover.DidNotReceive().CopyFileAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Func<long, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
#pragma warning restore CS4014
    }

    // ── MoveFile cross-volume — name collision ────────────────────────────────

    [Fact]
    public async Task MoveFile_cross_volume_name_collision_blocks_job()
    {
        var mover = DefaultMover();
        mover.When(m => m.FinalizePartial(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
             .Do(_ => throw new NameCollisionException(@"Backup\report.txt"));

        int jobId = SeedJob(
            JobType.MoveFile, JobState.Pending, intraVolume: false,
            srcVol: Vol1Id, tgtVol: Vol2Id, tgtPath: @"Backup\report.txt", totalBytes: 1_000,
            items:
            [
                new OperationJobItem
                {
                    SourceRelativePath = @"Docs\report.txt",
                    TargetRelativePath = @"Backup\report.txt",
                    State = JobItemState.Pending,
                    SizeBytes = 1_000,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                },
            ]);

        await MakeEngine(mover, feasible: true).ExecuteJobAsync(jobId, None);

        (await ReadState(jobId)).Should().Be(JobState.Blocked);
        (await ReadBlockReason(jobId)).Should().Be(JobBlockReason.NameCollision);
        mover.DidNotReceive().DeleteToRecycleBin(Arg.Any<string>(), Arg.Any<string>());
    }
}
