using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// WP2 — the product's founding promise (§1/§4): an operation queued against a volume that is
/// not connected WAITS for it. Covers the pre-execution offline gate (#3), the reservation that
/// must survive <c>Blocked(offline)</c>, the enqueue that is born Blocked instead of failing,
/// and the revaluation that restarts the job when the volume comes back (#13).
///
/// Real SQLite + real <see cref="SpaceLedger"/> (reservation bookkeeping is under test);
/// <see cref="IFileMover"/> substituted so "the mover was never called" is assertable.
/// </summary>
public sealed class JobOfflineGateTests : IDisposable
{
    private const int SrcVolId = 1;
    private const int TgtVolId = 2;
    private const int Dir1Id = 1;
    private const int File1Id = 1;
    private const string SrcGuid = @"\\?\Volume{aaa-1}\";
    private const string TgtGuid = @"\\?\Volume{bbb-2}\";
    private const long FileSize = 1_000;

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;

    public JobOfflineGateTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            Seed(setup);
        }

        var services = new ServiceCollection();
        var harness = _harness;
        services.AddScoped<FileTracertDbContext>(_ => harness.CreateContext());
        _ledger = new SpaceLedger(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SpaceLedger>.Instance);
    }

    public void Dispose() => _harness.Dispose();

    private static CancellationToken None => CancellationToken.None;

    // ── fixture ───────────────────────────────────────────────────────────────

    private static void Seed(FileTracertDbContext db)
    {
        db.Volumes.AddRange(
            new Volume
            {
                Id = SrcVolId, VolumeGuid = SrcGuid, Label = "Origine",
                FileSystem = "NTFS", FreeBytesLastKnown = 100_000, IsOnline = true,
            },
            new Volume
            {
                Id = TgtVolId, VolumeGuid = TgtGuid, Label = "Destinazione",
                FileSystem = "NTFS", FreeBytesLastKnown = 50_000, IsOnline = true,
            });

        db.Directories.Add(new DirectoryNode
        {
            Id = Dir1Id, VolumeId = SrcVolId, Name = "Docs",
            MaterializedPath = "Docs", IsMaterialized = true,
        });

        db.Files.Add(new FileEntry
        {
            Id = File1Id, VolumeId = SrcVolId, DirectoryId = Dir1Id,
            Name = "report.txt", Extension = "txt", Category = FileCategory.Document,
            SizeBytes = FileSize, IsPresent = true, IsIncluded = true,
            FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
            LastIndexedUtc = DateTime.UtcNow,
        });

        db.SaveChanges();
    }

    private void SetOnline(int volumeId, bool isOnline)
    {
        using var db = _harness.CreateContext();
        db.Volumes.Single(v => v.Id == volumeId).IsOnline = isOnline;
        db.SaveChanges();
    }

    private void SetFreeBytes(int volumeId, long freeBytes)
    {
        using var db = _harness.CreateContext();
        db.Volumes.Single(v => v.Id == volumeId).FreeBytesLastKnown = freeBytes;
        db.SaveChanges();
    }

    private static IFileMover FakeMover()
    {
        var mover = Substitute.For<IFileMover>();
        mover.CopyFileAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Func<long, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        mover.Verify(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
             .Returns(true);
        mover.Exists(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        mover.IsDirectoryEmpty(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        mover.CanRecycle(Arg.Any<string>()).Returns(true);
        return mover;
    }

    private JobExecutionEngine MakeEngine(IFileMover mover)
    {
        var db = _harness.CreateContext();
        return new JobExecutionEngine(
            db, mover, _ledger,
            new IndexUpdater(db, new FakeFileSearchIndex(), NullLogger<IndexUpdater>.Instance),
            new FileTracert.Business.Notifications.NotificationService(db),
            TimeProvider.System, NullLogger<JobExecutionEngine>.Instance);
    }

    private QueueService MakeQueue(IFileMover mover)
    {
        var db = _harness.CreateContext();
        return new QueueService(
            db, _ledger, new JobCancellationRegistry(), mover, new QueueSignal(),
            new IndexUpdater(db, new FakeFileSearchIndex(), NullLogger<IndexUpdater>.Instance),
            NullLogger<QueueService>.Instance);
    }

    private BlockedJobRevaluator MakeRevaluator() =>
        new(_harness.CreateContext(), _ledger, NullLogger<BlockedJobRevaluator>.Instance);

    /// <summary>Cross-volume MoveFile job, one item, sized <see cref="FileSize"/>.</summary>
    private int SeedCrossVolumeJob(
        JobState state = JobState.Pending,
        JobBlockReason reason = JobBlockReason.None,
        int? srcVol = SrcVolId,
        int? tgtVol = TgtVolId)
    {
        using var db = _harness.CreateContext();
        var job = new OperationJob
        {
            Type = JobType.MoveFile, State = state, BlockReason = reason,
            IsIntraVolume = false, SourceVolumeId = srcVol, TargetVolumeId = tgtVol,
            TargetRelativePath = @"Archivio\report.txt",
            TotalBytes = FileSize, RequiredBytesTarget = FileSize, FreedBytesSource = FileSize,
            SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        };
        job.Items.Add(new OperationJobItem
        {
            FileId = File1Id,
            SourceRelativePath = @"Docs\report.txt",
            TargetRelativePath = @"Archivio\report.txt",
            SizeBytes = FileSize, State = JobItemState.Pending,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        });
        db.OperationJobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    private async Task<OperationJob> ReadJob(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private async Task<List<SpaceLedgerEntry>> ActiveLedgerRows(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.SpaceLedgerEntries.AsNoTracking()
            .Where(e => e.JobId == jobId && e.IsActive).ToListAsync();
    }

    private static void MoverNeverTouchedTheDisk(IFileMover mover)
    {
        mover.DidNotReceiveWithAnyArgs().CopyFileAsync(default!, default!, default!, default!, default!, default);
        mover.DidNotReceiveWithAnyArgs().RenameIntraVolume(default!, default!, default!);
        mover.DidNotReceiveWithAnyArgs().MoveIntraVolume(default!, default!, default!);
        mover.DidNotReceiveWithAnyArgs().CreateFolder(default!, default!);
        mover.DidNotReceiveWithAnyArgs().DeleteToRecycleBin(default!, default!);
    }

    // ── #3 · pre-execution gate ───────────────────────────────────────────────

    [Fact]
    public async Task Job_with_offline_target_is_blocked_not_failed_and_never_reaches_the_mover()
    {
        SetOnline(TgtVolId, false);
        var mover = FakeMover();
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(mover).ExecuteJobAsync(jobId, None);

        var job = await ReadJob(jobId);
        job.State.Should().Be(JobState.Blocked);
        job.BlockReason.Should().Be(JobBlockReason.TargetVolumeOffline);
        MoverNeverTouchedTheDisk(mover);
    }

    [Fact]
    public async Task Job_with_offline_source_is_blocked_with_the_source_reason()
    {
        SetOnline(SrcVolId, false);
        var mover = FakeMover();
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(mover).ExecuteJobAsync(jobId, None);

        var job = await ReadJob(jobId);
        job.State.Should().Be(JobState.Blocked);
        job.BlockReason.Should().Be(JobBlockReason.SourceVolumeOffline);
        MoverNeverTouchedTheDisk(mover);
    }

    [Fact]
    public async Task Job_with_both_volumes_offline_reports_the_source_as_the_blocker()
    {
        SetOnline(SrcVolId, false);
        SetOnline(TgtVolId, false);
        var mover = FakeMover();
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(mover).ExecuteJobAsync(jobId, None);

        var job = await ReadJob(jobId);
        job.BlockReason.Should().Be(JobBlockReason.SourceVolumeOffline,
            "without the source there is nothing to read — it is reported first");
    }

    [Fact]
    public async Task Blocking_a_job_offline_keeps_its_space_reservation()
    {
        SetOnline(TgtVolId, false);
        int jobId = SeedCrossVolumeJob();
        await _ledger.ReserveAsync(jobId, 1, TgtVolId, FileSize, SrcVolId, FileSize, None);

        await MakeEngine(FakeMover()).ExecuteJobAsync(jobId, None);

        (await ActiveLedgerRows(jobId)).Should().HaveCount(2,
            "the demand must keep weighing on the target until the job really runs at the remount");
    }

    [Fact]
    public async Task Intra_volume_job_on_an_offline_volume_is_blocked_too()
    {
        SetOnline(SrcVolId, false);
        var mover = FakeMover();

        int jobId;
        using (var db = _harness.CreateContext())
        {
            var job = new OperationJob
            {
                Type = JobType.RenameFile, State = JobState.Pending, IsIntraVolume = true,
                SourceVolumeId = SrcVolId, TargetVolumeId = SrcVolId,
                TargetRelativePath = "report_v2.txt",
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            };
            job.Items.Add(new OperationJobItem
            {
                FileId = File1Id,
                SourceRelativePath = @"Docs\report.txt",
                TargetRelativePath = @"Docs\report_v2.txt",
                State = JobItemState.Pending,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        await MakeEngine(mover).ExecuteJobAsync(jobId, None);

        var reloaded = await ReadJob(jobId);
        reloaded.State.Should().Be(JobState.Blocked);
        reloaded.BlockReason.Should().Be(JobBlockReason.SourceVolumeOffline);
        MoverNeverTouchedTheDisk(mover);
    }

    // ── #3 · enqueue against an offline volume ────────────────────────────────

    [Fact]
    public async Task Enqueue_to_an_offline_target_is_born_Blocked_with_a_dead_estimate()
    {
        SetOnline(TgtVolId, false);

        var dto = await MakeQueue(FakeMover()).EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,
            TargetVolumeId = TgtVolId,
            TargetRelativePath = "Archivio",
        }, None);

        dto.State.Should().Be(nameof(JobState.Blocked));
        dto.BlockReason.Should().Be(nameof(JobBlockReason.TargetVolumeOffline));
        dto.EstimateIsLive.Should().BeFalse("the free space of an offline volume is a stale figure");
        (await ActiveLedgerRows(dto.Id)).Should().HaveCount(2,
            "a job waiting for its volume still competes for that volume's space");
    }

    [Fact]
    public async Task Enqueue_with_an_offline_source_is_born_Blocked_too()
    {
        SetOnline(SrcVolId, false);

        var dto = await MakeQueue(FakeMover()).EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,
            TargetVolumeId = TgtVolId,
            TargetRelativePath = "Archivio",
        }, None);

        dto.State.Should().Be(nameof(JobState.Blocked));
        dto.BlockReason.Should().Be(nameof(JobBlockReason.SourceVolumeOffline));
    }

    [Fact]
    public async Task Enqueue_of_an_intra_volume_op_on_an_offline_volume_is_born_Blocked()
    {
        SetOnline(TgtVolId, false);

        var dto = await MakeQueue(FakeMover()).EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder,
            TargetVolumeId = TgtVolId,
            TargetRelativePath = "Archivio",
        }, None);

        dto.State.Should().Be(nameof(JobState.Blocked));
        dto.BlockReason.Should().Be(nameof(JobBlockReason.TargetVolumeOffline));
    }

    // ── #13 · revaluation when the volume comes back ──────────────────────────

    [Fact]
    public async Task Revaluation_returns_an_offline_blocked_job_to_Pending_once_the_volume_is_back()
    {
        int jobId = SeedCrossVolumeJob(JobState.Blocked, JobBlockReason.TargetVolumeOffline);
        await _ledger.ReserveAsync(jobId, 1, TgtVolId, FileSize, SrcVolId, FileSize, None);

        // The volume is back, and the fresh probe brought a real free-space figure with it.
        SetOnline(TgtVolId, true);
        SetFreeBytes(TgtVolId, 50_000);

        int unblocked = await MakeRevaluator().RevaluateAsync(None);

        unblocked.Should().Be(1);
        var job = await ReadJob(jobId);
        job.State.Should().Be(JobState.Pending);
        job.BlockReason.Should().Be(JobBlockReason.None);
        (await ActiveLedgerRows(jobId)).Should().HaveCount(2, "exactly one reservation set, never stacked");
    }

    [Fact]
    public async Task Revaluation_leaves_the_job_blocked_while_the_volume_is_still_offline()
    {
        SetOnline(TgtVolId, false);
        int jobId = SeedCrossVolumeJob(JobState.Blocked, JobBlockReason.TargetVolumeOffline);

        int unblocked = await MakeRevaluator().RevaluateAsync(None);

        unblocked.Should().Be(0);
        var job = await ReadJob(jobId);
        job.State.Should().Be(JobState.Blocked);
        job.BlockReason.Should().Be(JobBlockReason.TargetVolumeOffline);
    }

    [Fact]
    public async Task Remount_with_less_free_space_than_estimated_keeps_the_job_blocked_on_space()
    {
        int jobId = SeedCrossVolumeJob(JobState.Blocked, JobBlockReason.TargetVolumeOffline);

        // Back online, but the drive came back fuller than the last known estimate: the hard
        // re-check must catch it instead of starting a copy that cannot fit (§4).
        SetOnline(TgtVolId, true);
        SetFreeBytes(TgtVolId, FileSize - 1);

        int unblocked = await MakeRevaluator().RevaluateAsync(None);

        unblocked.Should().Be(0);
        var job = await ReadJob(jobId);
        job.State.Should().Be(JobState.Blocked);
        job.BlockReason.Should().Be(JobBlockReason.InsufficientSpace);
    }

    [Fact]
    public async Task Revaluation_retargets_the_reason_when_only_the_other_volume_is_still_missing()
    {
        SetOnline(SrcVolId, false);
        int jobId = SeedCrossVolumeJob(JobState.Blocked, JobBlockReason.TargetVolumeOffline);

        int unblocked = await MakeRevaluator().RevaluateAsync(None);

        unblocked.Should().Be(0);
        var job = await ReadJob(jobId);
        job.BlockReason.Should().Be(JobBlockReason.SourceVolumeOffline,
            "the reason must name the volume that is missing NOW, or the UI lies about what to plug back in");
    }

    [Fact]
    public async Task A_job_blocked_on_space_whose_volume_went_offline_is_not_unblocked_by_the_estimate()
    {
        int jobId = SeedCrossVolumeJob(JobState.Blocked, JobBlockReason.InsufficientSpace);
        SetOnline(TgtVolId, false);
        SetFreeBytes(TgtVolId, 50_000);   // stale figure that says "it fits"

        int unblocked = await MakeRevaluator().RevaluateAsync(None);

        unblocked.Should().Be(0);
        var job = await ReadJob(jobId);
        job.State.Should().Be(JobState.Blocked);
        job.BlockReason.Should().Be(JobBlockReason.TargetVolumeOffline);
    }
}
