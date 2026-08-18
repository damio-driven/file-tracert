using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Platform;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// Reproduces review finding #2: <c>TransitionAsync</c> was a blind UPDATE by PK, so a
/// <c>Cancelled</c> committed by the API's DbContext in the window between the engine's re-read
/// and its <c>SaveChanges</c> got overwritten — and the engine went on to recycle the sources of
/// a job the user had cancelled. The fix is a concurrency token on <c>OperationJob.State</c>:
/// the stale transition must fail, the engine must follow the committed state.
/// Real <see cref="Win32FileMover"/>, real SQLite, the race injected deterministically via a
/// SaveChanges interceptor — no mock of the component under test.
/// </summary>
[Trait("Category", "Platform")]
public sealed class JobStateConcurrencyTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;
    private readonly Win32FileMover _mover;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public JobStateConcurrencyTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

        _mover = new Win32FileMover(NullLogger<Win32FileMover>.Instance);

        var probe = new Win32VolumeProbe(
            new WmiPhysicalDiskResolver(NullLogger<WmiPhysicalDiskResolver>.Instance),
            NullLogger<Win32VolumeProbe>.Instance);

        var tempPath = Path.GetTempPath();
        var vol = probe.EnumerateVolumes()
            .Where(v => v.MountPoints.Count > 0)
            .OrderByDescending(v => v.MountPoints[0].Length)
            .First(v => tempPath.StartsWith(v.MountPoints[0], StringComparison.OrdinalIgnoreCase));

        _volumeGuid = vol.VolumeGuid;
        _mountPoint = vol.MountPoints[0];
        _absRoot = Path.Combine(tempPath, $"ft-conc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_absRoot);
        _relRoot = Path.GetRelativePath(_mountPoint, _absRoot);
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_absRoot))
            Directory.Delete(_absRoot, recursive: true);
    }

    private string R(params string[] parts) => Path.Combine([_relRoot, .. parts]);
    private string Abs(string rel) => Path.GetFullPath(Path.Combine(_mountPoint, rel));

    private JobExecutionEngine MakeEngine(FileTracertDbContext db)
    {
        var ledger = Substitute.For<ISpaceLedger>();
        ledger.ReleaseAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);
        var indexUpdater = TestProjection.Index(db);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db, TestProjection.Realtime());
        return new JobExecutionEngine(db, _mover, ledger, indexUpdater, TestProjection.Overlay(db), notifications,
            TimeProvider.System, TestProjection.Realtime(), NullLogger<JobExecutionEngine>.Instance);
    }

    [Fact]
    public async Task Cancel_committed_between_reread_and_transition_save_is_never_overwritten()
    {
        // Job checkpointed at Verifying with its single item already finalized (Verified,
        // partial gone, final on the target). The engine's next persisted step is the
        // Verifying → DeletingSource transition; the cancel lands exactly inside it.
        const string content = "precious source data";
        var srcRel = R("src", "keep.txt");
        var dstRel = R("dst", "keep.txt");

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(dstRel), content); // already finalized by the previous run

        int jobId;
        using (var db = _harness.CreateContext())
        {
            db.Volumes.Add(new Volume
            {
                Id = 1, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
                FreeBytesLastKnown = 1_000_000, IsOnline = true,
            });
            var job = new OperationJob
            {
                Id = 1, Type = JobType.MoveFile, State = JobState.Verifying,
                IsIntraVolume = false, SourceVolumeId = 1, TargetVolumeId = 1,
                TargetRelativePath = dstRel, TotalBytes = content.Length, RequiredBytesTarget = content.Length,
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            };
            job.Items.Add(new OperationJobItem
            {
                SourceRelativePath = srcRel, TargetRelativePath = dstRel,
                SizeBytes = content.Length, State = JobItemState.Verified,
                BytesCopied = content.Length,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        // The interceptor fires on the engine's own SaveChanges that would persist
        // State=DeletingSource — i.e. AFTER the engine's cancellation re-read said "not
        // cancelled" — and commits Cancelled through a different DbContext first.
        var interceptor = new CancelInjectingInterceptor(jobId, () =>
        {
            using var db = _harness.CreateContext();
            var job = db.OperationJobs.Single(j => j.Id == jobId);
            job.State = JobState.Cancelled;
            job.CompletedUtc = DateTime.UtcNow;
            db.SaveChanges();
        });

        var engineDb = _harness.CreateContext(interceptor);
        await MakeEngine(engineDb).ExecuteJobAsync(jobId, CancellationToken.None);

        interceptor.Injected.Should().BeTrue("the race window must actually have been exercised");

        // The user's cancel must win: the source is never recycled, the state stays Cancelled.
        File.Exists(Abs(srcRel)).Should().BeTrue("a cancelled job must never touch its source");
        File.ReadAllText(Abs(srcRel)).Should().Be(content);

        using var check = _harness.CreateContext();
        var state = await check.OperationJobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync();
        state.Should().Be(JobState.Cancelled);
    }

    /// <summary>
    /// Fires <paramref name="commitCancel"/> once, right before the SaveChanges that would
    /// move the watched job to <see cref="JobState.DeletingSource"/> — the exact blind-update
    /// window of finding #2.
    /// </summary>
    private sealed class CancelInjectingInterceptor : SaveChangesInterceptor
    {
        private readonly int _jobId;
        private readonly Action _commitCancel;

        public CancelInjectingInterceptor(int jobId, Action commitCancel)
        {
            _jobId = jobId;
            _commitCancel = commitCancel;
        }

        public bool Injected { get; private set; }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            InjectIfTransitionToDeleting(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            InjectIfTransitionToDeleting(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void InjectIfTransitionToDeleting(DbContextEventData eventData)
        {
            if (Injected || eventData.Context is null) return;

            bool isTheTransition = eventData.Context.ChangeTracker.Entries<OperationJob>().Any(e =>
                e.Entity.Id == _jobId &&
                e.State == EntityState.Modified &&
                e.Entity.State == JobState.DeletingSource);

            if (!isTheTransition) return;

            Injected = true;
            _commitCancel();
        }
    }
}
