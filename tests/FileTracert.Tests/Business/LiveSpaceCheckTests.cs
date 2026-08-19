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
/// Finding 10 — the hard space re-check must compare the demand against what the DEVICE reports,
/// not against the <c>FreeBytesLastKnown</c> the last volume sync wrote. Between that sync and
/// the copy another process can have written tens of gigabytes: trusting the stored number is
/// exactly the "copiare sulla fiducia di una stima" §4 forbids.
///
/// Everything below runs the REAL ledger, the REAL engine and a real SQLite database; only the
/// platform port (<see cref="IVolumeProbe"/>) and the file mover are substituted — the components
/// under test are the ledger and the engine, not Win32.
/// </summary>
public sealed class LiveSpaceCheckTests : IDisposable
{
    private const int SourceVolumeId = 1;
    private const int TargetVolumeId = 2;
    private const int DirId = 1;
    private const int FileId = 1;
    private const string SourceGuid = @"\\?\Volume{aaa-1}\";
    private const string TargetGuid = @"\\?\Volume{bbb-2}\";

    /// <summary>What the catalog believes about the target: plenty of room.</summary>
    private const long LastKnownFreeBytes = 1_000_000;

    private const long MoveSizeBytes = 100_000;

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;

    public LiveSpaceCheckTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            Seed(setup);
        }
        _ledger = new SpaceLedger(ScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
    }

    public void Dispose() => _harness.Dispose();

    // ── the engine's hard re-check ────────────────────────────────────────────

    [Fact]
    public async Task Disk_fuller_than_the_catalog_believes_blocks_the_job()
    {
        // The catalog says 1 000 000 free; the drive really has 1 000 — a mismatch that in
        // production means another process filled the volume after the last sync.
        var probe = new StubFreeSpaceProbe(1_000);
        var mover = FeasibleMover();
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(mover, probe).ExecuteJobAsync(jobId, CancellationToken.None);

        (await ReadJobAsync(jobId)).State.Should().Be(JobState.Blocked,
            "the re-check must believe the disk, not the stored estimate");
        (await ReadJobAsync(jobId)).BlockReason.Should().Be(JobBlockReason.InsufficientSpace);
        NothingWasCopied(mover);
    }

    [Fact]
    public async Task Disk_roomier_than_the_catalog_believes_lets_the_job_run()
    {
        // The stored estimate would refuse a 100 000-byte move; the drive really has room.
        await SetLastKnownFreeBytesAsync(10_000);
        var probe = new StubFreeSpaceProbe(5_000_000);
        var mover = FeasibleMover();
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(mover, probe).ExecuteJobAsync(jobId, CancellationToken.None);

        (await ReadJobAsync(jobId)).State.Should().Be(JobState.Completed,
            "a stale pessimistic estimate must not park a job that fits");
    }

    [Fact]
    public async Task Volume_that_does_not_answer_the_probe_is_blocked_never_failed()
    {
        // The offline gate normally answers first; this is the volume flagged online that the
        // device layer cannot measure anyway.
        var probe = new StubFreeSpaceProbe(null);
        var mover = FeasibleMover();
        int jobId = SeedCrossVolumeJob();
        await _ledger.ReserveAsync(jobId, sequenceOrder: 1, TargetVolumeId, MoveSizeBytes,
            SourceVolumeId, MoveSizeBytes, CancellationToken.None);

        await MakeEngine(mover, probe).ExecuteJobAsync(jobId, CancellationToken.None);

        var job = await ReadJobAsync(jobId);
        job.State.Should().Be(JobState.Blocked, "an unreadable volume is recoverable, not a failure");
        job.BlockReason.Should().Be(JobBlockReason.TargetVolumeOffline);
        job.ErrorMessage.Should().NotBeNullOrWhiteSpace("a block the user can act on must say why");
        NothingWasCopied(mover);

        await using var db = _harness.CreateContext();
        (await db.SpaceLedgerEntries.CountAsync(e => e.JobId == jobId && e.IsActive))
            .Should().Be(2, "a parked job keeps its reservation — it will run at the remount");
    }

    [Fact]
    public async Task The_probed_figure_replaces_the_last_known_one()
    {
        var probe = new StubFreeSpaceProbe(777_000);
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(FeasibleMover(), probe).ExecuteJobAsync(jobId, CancellationToken.None);

        await using var db = _harness.CreateContext();
        var target = await db.Volumes.SingleAsync(v => v.Id == TargetVolumeId);
        // An absolute overwrite, and nothing else: no completion arithmetic on top of it, or the
        // stored estimate would drift one job size below the truth until the next probe.
        target.FreeBytesLastKnown.Should().Be(777_000,
            "the column holds what was measured, not a measurement minus a guess");
    }

    [Fact]
    public async Task A_completed_job_re_measures_instead_of_guessing_what_it_consumed()
    {
        // The drive shrinks while the job runs, exactly as the landing bytes make it shrink.
        var probe = new StubFreeSpaceProbe(900_000);
        probe.AfterProbe = () => probe.FreeBytes = 800_000;
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(FeasibleMover(), probe).ExecuteJobAsync(jobId, CancellationToken.None);

        await using var db = _harness.CreateContext();
        var target = await db.Volumes.SingleAsync(v => v.Id == TargetVolumeId);
        target.FreeBytesLastKnown.Should().Be(800_000,
            "after the copy the column must hold a NEW reading, not the one taken before it");
    }

    [Fact]
    public async Task The_decision_is_taken_on_one_snapshot_of_the_drive()
    {
        // Blocked: nothing was copied, so nothing changed and there is nothing to re-measure.
        var probe = new StubFreeSpaceProbe(1_000);
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(FeasibleMover(), probe).ExecuteJobAsync(jobId, CancellationToken.None);

        (await ReadJobAsync(jobId)).State.Should().Be(JobState.Blocked);
        probe.Probes.Should().Be(1,
            "the verdict and the stored figure must come from ONE reading, not two that disagree");
    }

    [Fact]
    public void A_volume_the_catalog_knows_is_gone_is_not_asked()
    {
        var probe = new StubFreeSpaceProbe(5_000_000);
        var volume = new Volume
        {
            Id = 99, VolumeGuid = @"\\?\Volume{ccc-3}\", FileSystem = "NTFS",
            FreeBytesLastKnown = 4_242, IsOnline = false,
        };

        using var db = _harness.CreateContext();
        var space = TestProjection.Space(db, _ledger, probe).ReadFreeSpace(volume);

        probe.Probes.Should().Be(0,
            "asking a device the catalog already knows is disconnected costs a syscall that can " +
            "stall, and a warning line, for an answer nobody disputes");
        space.FreeBytes.Should().Be(4_242);
        space.IsLive.Should().BeFalse();
    }

    // ── the safety margin (§4: "free space + margin") ─────────────────────────

    [Fact]
    public async Task The_margin_blocks_a_move_that_would_fit_without_it()
    {
        // Exactly enough room for the payload and not one byte more.
        await SetMarginPercentAsync(3);
        var probe = new StubFreeSpaceProbe(MoveSizeBytes);
        var mover = FeasibleMover();
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(mover, probe).ExecuteJobAsync(jobId, CancellationToken.None);

        var job = await ReadJobAsync(jobId);
        job.State.Should().Be(JobState.Blocked, "3% of the demand is missing");
        job.BlockReason.Should().Be(JobBlockReason.InsufficientSpace);
        job.ErrorMessage.Should().Contain("margin", "a block on the cushion must name the cushion");
        NothingWasCopied(mover);
    }

    [Fact]
    public async Task With_no_margin_the_same_move_runs()
    {
        await SetMarginPercentAsync(0);
        var probe = new StubFreeSpaceProbe(MoveSizeBytes);
        int jobId = SeedCrossVolumeJob();

        await MakeEngine(FeasibleMover(), probe).ExecuteJobAsync(jobId, CancellationToken.None);

        (await ReadJobAsync(jobId)).State.Should().Be(JobState.Completed,
            "with the knob at zero the check is exactly the demand");
    }

    [Fact]
    public async Task An_absurd_margin_setting_is_clamped_instead_of_parking_everything()
    {
        await SetMarginPercentAsync(300);

        await using var db = _harness.CreateContext();
        var check = TestProjection.Space(db, _ledger, new StubFreeSpaceProbe(MoveSizeBytes));

        (await check.MarginPercentAsync(CancellationToken.None)).Should().Be(50);
    }

    [Fact]
    public void The_margin_is_a_percentage_of_the_demand_not_of_the_drive()
    {
        SpaceCheck.MarginBytesFor(1_000_000, 3).Should().Be(30_000);
        SpaceCheck.MarginBytesFor(1_000_000, 0).Should().Be(0);
        SpaceCheck.MarginBytesFor(0, 5).Should().Be(0);
    }

    // ── the revaluation's hard re-check ───────────────────────────────────────

    [Fact]
    public async Task Revaluation_keeps_a_job_blocked_when_the_disk_is_fuller_than_the_catalog_believes()
    {
        int jobId = SeedCrossVolumeJob(JobState.Blocked, JobBlockReason.InsufficientSpace);
        var probe = new StubFreeSpaceProbe(1_000);

        await using var db = _harness.CreateContext();
        int released = await TestProjection.Revaluator(db, _ledger, fts: null, probe)
            .RevaluateAsync(CancellationToken.None);

        released.Should().Be(0, "releasing on a stale number only makes the engine block it again");
        (await ReadJobAsync(jobId)).State.Should().Be(JobState.Blocked);
    }

    [Fact]
    public async Task Revaluation_releases_a_job_the_disk_can_now_hold()
    {
        int jobId = SeedCrossVolumeJob(JobState.Blocked, JobBlockReason.InsufficientSpace);
        await SetLastKnownFreeBytesAsync(0);   // the catalog is out of date in the pessimistic direction
        var probe = new StubFreeSpaceProbe(5_000_000);

        await using var db = _harness.CreateContext();
        int released = await TestProjection.Revaluator(db, _ledger, fts: null, probe)
            .RevaluateAsync(CancellationToken.None);

        released.Should().Be(1);
        (await ReadJobAsync(jobId)).State.Should().Be(JobState.Pending);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static IServiceScopeFactory ScopeFactory(SqliteInMemoryContext harness)
    {
        var services = new ServiceCollection();
        services.AddScoped<FileTracertDbContext>(_ => harness.CreateContext());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static void Seed(FileTracertDbContext db)
    {
        db.Volumes.AddRange(
            new Volume
            {
                Id = SourceVolumeId, VolumeGuid = SourceGuid, FileSystem = "NTFS",
                FreeBytesLastKnown = LastKnownFreeBytes, IsOnline = true,
            },
            new Volume
            {
                Id = TargetVolumeId, VolumeGuid = TargetGuid, FileSystem = "NTFS",
                FreeBytesLastKnown = LastKnownFreeBytes, IsOnline = true,
            });

        db.Directories.Add(new DirectoryNode
        {
            Id = DirId, VolumeId = SourceVolumeId, Name = "Docs",
            MaterializedPath = "Docs", IsMaterialized = true,
        });

        db.Files.Add(new FileEntry
        {
            Id = FileId, VolumeId = SourceVolumeId, DirectoryId = DirId,
            Name = "payload.bin", Extension = "bin", Category = FileCategory.Other,
            SizeBytes = MoveSizeBytes, IsPresent = true, IsIncluded = true,
            FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
            LastIndexedUtc = DateTime.UtcNow,
        });

        db.SaveChanges();
    }

    private int SeedCrossVolumeJob(
        JobState state = JobState.Pending, JobBlockReason reason = JobBlockReason.None)
    {
        using var db = _harness.CreateContext();
        var job = new OperationJob
        {
            Type = JobType.MoveFile,
            State = state,
            BlockReason = reason,
            IsIntraVolume = false,
            SourceVolumeId = SourceVolumeId,
            TargetVolumeId = TargetVolumeId,
            TargetRelativePath = @"Backup\payload.bin",
            TotalBytes = MoveSizeBytes,
            RequiredBytesTarget = MoveSizeBytes,
            FreedBytesSource = MoveSizeBytes,
            SequenceOrder = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        job.Items.Add(new OperationJobItem
        {
            FileId = FileId,
            SourceRelativePath = @"Docs\payload.bin",
            TargetRelativePath = @"Backup\payload.bin",
            SizeBytes = MoveSizeBytes,
            State = JobItemState.Pending,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        db.OperationJobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    private JobExecutionEngine MakeEngine(IFileMover mover, IVolumeProbe probe)
    {
        var db = _harness.CreateContext();
        return new JobExecutionEngine(
            db, mover, _ledger, TestProjection.Space(db, _ledger, probe),
            TestProjection.Index(db), TestProjection.Overlay(db),
            new FileTracert.Business.Notifications.NotificationService(db, TestProjection.Realtime()),
            TimeProvider.System, TestProjection.Realtime(), NullLogger<JobExecutionEngine>.Instance);
    }

    /// <summary>A mover for which every copy, verify and delete succeeds.</summary>
    private static IFileMover FeasibleMover()
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

    private static void NothingWasCopied(IFileMover mover)
    {
#pragma warning disable CS4014
        mover.DidNotReceive().CopyFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Func<long, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
#pragma warning restore CS4014
    }

    private async Task SetMarginPercentAsync(int percent)
    {
        await using var db = _harness.CreateContext();
        var settings = await db.AppSettings.FirstOrDefaultAsync();
        if (settings is null)
        {
            db.AppSettings.Add(new AppSettings
            {
                ApiToken = "token", SpaceMarginPercent = percent,
                DefaultExtensionFilter = [], ExcludedPaths = [],
            });
        }
        else
        {
            settings.SpaceMarginPercent = percent;
        }
        await db.SaveChangesAsync();
    }

    private async Task SetLastKnownFreeBytesAsync(long bytes)
    {
        await using var db = _harness.CreateContext();
        (await db.Volumes.SingleAsync(v => v.Id == TargetVolumeId)).FreeBytesLastKnown = bytes;
        await db.SaveChangesAsync();
    }

    private async Task<OperationJob> ReadJobAsync(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }
}
