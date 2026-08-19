using FileTracert.Business.Notifications;
using FileTracert.Business.Operations;
using FileTracert.Business.Realtime;
using FileTracert.Business.Scanning;
using FileTracert.Contracts.Scanning;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Realtime;
using FileTracert.Data.Entities;
using FileTracert.Data;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// Step 10b — the emitters, against the real implementations (real engine, real queue service,
/// real ledger, real SQLite): only the transport is a fake, because the transport is the thing
/// under observation. Two questions per emitter: does it publish the right payload, and can its
/// failure hurt the operation that produced it (it must not — §9).
/// </summary>
public sealed class RealtimeEmissionTests : IDisposable
{
    private const int Vol1Id = 1;
    private const int Vol2Id = 2;
    private const int Dir1Id = 1;
    private const int File1Id = 1;
    private const string Vol1Guid = @"\\?\Volume{aaa-1}\";
    private const string Vol2Guid = @"\\?\Volume{bbb-2}\";

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;

    public RealtimeEmissionTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            Seed(setup);
        }

        var services = new ServiceCollection();
        services.AddScoped<FileTracertDbContext>(_ => _harness.CreateContext());
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _ledger = new SpaceLedger(scopes, NullLogger<SpaceLedger>.Instance);
    }

    public void Dispose() => _harness.Dispose();

    private static CancellationToken None => CancellationToken.None;

    // ── job state + progress ──────────────────────────────────────────────────

    [Fact]
    public async Task Cross_volume_move_publishes_every_transition_progress_and_the_projection_clear()
    {
        var spy = new RecordingRealtimePublisher();
        int jobId = SeedCrossVolumeMove();

        await MakeEngine(DefaultMover(), spy).ExecuteJobAsync(jobId, None);

        var states = spy.Of<JobStateChanged>();
        states.Should().OnlyContain(m => m.JobId == jobId);
        states.Select(m => m.State).Should().ContainInOrder(
            JobState.SpaceReserved, JobState.Copying, JobState.Verifying,
            JobState.DeletingSource, JobState.Completed);

        var progress = spy.Of<JobProgress>();
        progress.Should().NotBeEmpty("the copy loop must report bytes as it goes");
        progress[^1].BytesProcessed.Should().Be(1_000);
        progress[^1].TotalBytes.Should().Be(1_000);

        // Completed cleared the overlay in the same transaction (§5), so the projection moved too.
        spy.Of<ProjectionChanged>().Should().ContainSingle().Which.JobId.Should().Be(jobId);
    }

    [Fact]
    public async Task A_cross_volume_job_names_no_single_volume_on_ProjectionChanged()
    {
        var spy = new RecordingRealtimePublisher();
        int jobId = SeedCrossVolumeMove();

        await MakeEngine(DefaultMover(), spy).ExecuteJobAsync(jobId, None);

        // Two volumes changed; naming one would let a client refresh the wrong half.
        spy.Of<ProjectionChanged>().Should().ContainSingle().Which.VolumeId.Should().BeNull();
    }

    [Fact]
    public async Task A_publisher_that_throws_does_not_stop_the_job_from_completing()
    {
        var broken = new ThrowingRealtimePublisher();
        int jobId = SeedCrossVolumeMove();

        await MakeEngine(DefaultMover(), broken).ExecuteJobAsync(jobId, None);

        broken.Attempts.Should().BeGreaterThan(0, "the engine really did try to publish");
        (await ReadState(jobId)).Should().Be(JobState.Completed);
    }

    [Fact]
    public async Task A_blocked_job_announces_its_state_but_not_a_projection_change()
    {
        var spy = new RecordingRealtimePublisher();
        SetVolumeOnline(Vol2Id, online: false);
        int jobId = SeedCrossVolumeMove();

        await MakeEngine(DefaultMover(), spy).ExecuteJobAsync(jobId, None);

        var blocked = spy.Of<JobStateChanged>().Should().ContainSingle().Subject;
        blocked.State.Should().Be(JobState.Blocked);
        blocked.BlockReason.Should().Be(JobBlockReason.TargetVolumeOffline);
        blocked.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        // §5: Blocked KEEPS its overlay, so the projection did not change.
        spy.Of<ProjectionChanged>().Should().BeEmpty();
    }

    [Fact]
    public async Task Enqueue_publishes_the_new_job_and_its_projection()
    {
        var spy = new RecordingRealtimePublisher();
        var queue = MakeQueue(spy);

        var dto = await queue.EnqueueAsync(
            new CreateJobRequest
            {
                Type = JobType.CreateFolder,
                TargetVolumeId = Vol1Id,
                TargetRelativePath = "Docs/New",
            }, None);

        var state = spy.Of<JobStateChanged>().Should().ContainSingle().Subject;
        state.JobId.Should().Be(dto.Id);
        state.State.Should().Be(JobState.Pending);

        var projection = spy.Of<ProjectionChanged>().Should().ContainSingle().Subject;
        projection.JobId.Should().Be(dto.Id);
        projection.VolumeId.Should().Be(Vol1Id);
    }

    // ── notifications ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_notification_is_announced_with_the_id_it_was_saved_under()
    {
        var spy = new RecordingRealtimePublisher();
        await using var db = _harness.CreateContext();
        var service = new NotificationService(db, Events(spy));

        await service.PublishAsync(
            NotificationSeverity.Error, "Coda", "Operazione fallita", "dettaglio reale", Vol1Id, None);

        var saved = await db.Notifications.AsNoTracking().SingleAsync();
        var raised = spy.Of<NotificationRaised>().Should().ContainSingle().Subject;
        raised.Id.Should().Be(saved.Id);
        raised.Id.Should().NotBe(0, "the push happens after the save, so the id already exists");
        raised.Severity.Should().Be(NotificationSeverity.Error);
        raised.Title.Should().Be("Operazione fallita");
        raised.TimestampUtc.Should().Be(saved.TimestampUtc);
    }

    // ── volumes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_volume_going_offline_is_announced_and_a_quiet_cycle_says_nothing()
    {
        var spy = new RecordingRealtimePublisher();

        // Nothing probed, so both seeded volumes go offline.
        await MakeSync(new FakeVolumesProbe([]), spy).SyncAsync(None);

        var pushes = spy.Of<VolumeStatusChanged>();
        pushes.Select(m => m.VolumeId).Should().BeEquivalentTo(new[] { Vol1Id, Vol2Id });
        pushes.Should().OnlyContain(m => !m.IsOnline);

        // Second identical cycle: nothing moved, so nothing is pushed.
        var quiet = new RecordingRealtimePublisher();
        await MakeSync(new FakeVolumesProbe([]), quiet).SyncAsync(None);
        quiet.Messages.Should().BeEmpty();
    }

    // ── scan progress ─────────────────────────────────────────────────────────

    [Fact]
    public void Scan_counters_are_throttled_while_phase_changes_and_the_end_are_not()
    {
        var spy = new RecordingRealtimePublisher();
        var clock = new ManualClock();
        var tracker = new ScanStatusTracker(Events(spy), clock);

        tracker.Begin(Vol1Id, "D");                       // 1 — the scan started
        for (int i = 0; i < 500; i++)
        {
            tracker.ReportSeen(Vol1Id, i);                // throttled away: no time passed
        }

        spy.Of<ScanStatusDto>().Should().HaveCount(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.ReportSeen(Vol1Id, 500);                  // 2 — the window elapsed
        tracker.ReportSeen(Vol1Id, 501);                  // throttled again
        tracker.SetPhase(Vol1Id, ScanPhase.Writing);      // 3 — a phase change never waits
        tracker.Complete(Vol1Id);                         // 4 — the terminal frame

        var frames = spy.Of<ScanStatusDto>();
        frames.Should().HaveCount(4);
        frames[0].Phase.Should().Be(ScanPhase.Enumerating);
        frames[1].ItemsSeen.Should().Be(500);
        frames[2].Phase.Should().Be(ScanPhase.Writing);
        frames[3].Phase.Should().Be(ScanPhase.Done);
        tracker.Snapshot().Should().BeEmpty("a finished scan is no longer in flight");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RealtimeEvents Events(IRealtimePublisher publisher) =>
        new(publisher, NullLogger<RealtimeEvents>.Instance);

    private JobExecutionEngine MakeEngine(IFileMover mover, IRealtimePublisher publisher)
    {
        var db = _harness.CreateContext();
        return new JobExecutionEngine(
            db, mover, _ledger, TestProjection.Space(db, _ledger), TestProjection.Index(db), TestProjection.Overlay(db),
            new NotificationService(db, Events(publisher)), TimeProvider.System,
            Events(publisher), NullLogger<JobExecutionEngine>.Instance);
    }

    private QueueService MakeQueue(IRealtimePublisher publisher)
    {
        var db = _harness.CreateContext();
        return new QueueService(
            db, _ledger, TestProjection.Space(db, _ledger), new JobCancellationRegistry(), Substitute.For<IFileMover>(), new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db), TestProjection.Unblocker(db),
            TestProjection.Revaluator(db, _ledger), Events(publisher), NullLogger<QueueService>.Instance);
    }

    private VolumeSyncService MakeSync(IVolumeProbe probe, IRealtimePublisher publisher) =>
        new(probe, _harness.CreateContext(), Events(publisher), NullLogger<VolumeSyncService>.Instance);

    private int SeedCrossVolumeMove()
    {
        using var db = _harness.CreateContext();
        var job = new OperationJob
        {
            Type = JobType.MoveFile,
            State = JobState.Pending,
            IsIntraVolume = false,
            SourceVolumeId = Vol1Id,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Inbox",
            TotalBytes = 1_000,
            RequiredBytesTarget = 1_000,
            FreedBytesSource = 1_000,
            SequenceOrder = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        job.Items.Add(new OperationJobItem
        {
            FileId = File1Id,
            SourceRelativePath = "Docs/report.txt",
            TargetRelativePath = "Inbox/report.txt",
            SizeBytes = 1_000,
            State = JobItemState.Pending,
        });
        db.OperationJobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    private void SetVolumeOnline(int volumeId, bool online)
    {
        using var db = _harness.CreateContext();
        db.Volumes.Single(v => v.Id == volumeId).IsOnline = online;
        db.SaveChanges();
    }

    private async Task<JobState> ReadState(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync();
    }

    private static IFileMover DefaultMover()
    {
        var mover = Substitute.For<IFileMover>();
        mover.CopyFileAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Func<long, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
             .Returns(call => call.Arg<Func<long, CancellationToken, Task>>()!(1_000, None));
        mover.Verify(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
             .Returns(true);
        mover.Exists(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        mover.IsDirectoryEmpty(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        mover.CanRecycle(Arg.Any<string>()).Returns(true);
        return mover;
    }

    private static void Seed(FileTracertDbContext db)
    {
        db.Volumes.AddRange(
            new Volume
            {
                Id = Vol1Id, VolumeGuid = Vol1Guid, FileSystem = "NTFS",
                FreeBytesLastKnown = 100_000, IsOnline = true,
            },
            new Volume
            {
                Id = Vol2Id, VolumeGuid = Vol2Guid, FileSystem = "NTFS",
                FreeBytesLastKnown = 100_000, IsOnline = true,
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

    /// <summary>Hand-advanced clock: the throttle is tested by moving time, never by sleeping.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp = 1;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }
}
