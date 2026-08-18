using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Platform;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// FIX #10-partial — a job that ends <c>Failed</c> (engine) or <c>Cancelled</c> (API, job not
/// running) must not leave orphan <c>.fadit-partial</c> files on the target volume. Tests run
/// against the REAL <see cref="Win32FileMover"/> on temp directories (the cleanup I/O is what's
/// under test). Cross-volume is simulated on one physical volume (IsIntraVolume=false).
/// </summary>
[Trait("Category", "Platform")]
public sealed class JobPartialCleanupTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;
    private readonly Win32FileMover _mover;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public JobPartialCleanupTests()
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
        _absRoot = Path.Combine(tempPath, $"ft-partial-{Guid.NewGuid():N}");
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

    private JobExecutionEngine MakeEngine()
    {
        var ledger = Substitute.For<ISpaceLedger>();
        ledger.ReleaseAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);
        // Always-feasible: the space check is not under test here, the cleanup I/O is.
        ledger.ComputeFeasibilityAsync(default, default, default, default, default, default, default, default)
              .ReturnsForAnyArgs(new FeasibilityResult(0, 0, long.MaxValue, 0, true, null, true));

        var db = _harness.CreateContext();
        var indexUpdater = TestProjection.Index(db);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db, TestProjection.Realtime());
        return new JobExecutionEngine(db, _mover, ledger, indexUpdater, TestProjection.Overlay(db), notifications,
            TimeProvider.System, TestProjection.Realtime(), NullLogger<JobExecutionEngine>.Instance);
    }

    private QueueService MakeQueue()
    {
        var ledger = Substitute.For<ISpaceLedger>();
        ledger.ReleaseAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);
        var db = _harness.CreateContext();
        return new QueueService(db, ledger, new JobCancellationRegistry(),
            _mover, new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db),
            TestProjection.Unblocker(db),
            TestProjection.Revaluator(db, ledger),
            TestProjection.Realtime(), NullLogger<QueueService>.Instance);
    }

    private void SeedVolume()
    {
        using var db = _harness.CreateContext();
        db.Volumes.Add(new Volume
        {
            Id = 1, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
            FreeBytesLastKnown = 1_000_000, IsOnline = true,
        });
        db.SaveChanges();
    }

    private int SeedJob(JobState state, params OperationJobItem[] items)
    {
        using var db = _harness.CreateContext();
        var job = new OperationJob
        {
            Id = 1, Type = JobType.MoveFolder, State = state,
            IsIntraVolume = false, SourceVolumeId = 1, TargetVolumeId = 1,
            TargetRelativePath = R("dst"),
            TotalBytes = items.Sum(i => i.SizeBytes), RequiredBytesTarget = items.Sum(i => i.SizeBytes),
            SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        };
        foreach (var item in items)
            job.Items.Add(item);
        db.OperationJobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    private static OperationJobItem Item(string srcRel, string dstRel, long size,
        JobItemState state = JobItemState.Pending, string? tempPath = null) => new()
    {
        SourceRelativePath = srcRel, TargetRelativePath = dstRel, SizeBytes = size,
        State = state, TempPath = tempPath,
        CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
    };

    private List<string> PartialsOnDisk() =>
        Directory.Exists(Abs(R("dst")))
            ? Directory.EnumerateFiles(Abs(R("dst")), "*.fadit-partial", SearchOption.AllDirectories).ToList()
            : [];

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Job_that_fails_mid_copy_leaves_no_partial_on_target()
    {
        const string content = "first file copies fine";
        var src1 = R("src", "ok.txt");
        var src2 = R("src", "missing.txt"); // never created → real copy throws → job Failed

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(src1), content);
        Directory.CreateDirectory(Abs(R("dst")));

        SeedVolume();
        int jobId = SeedJob(JobState.Pending,
            Item(src1, R("dst", "ok.txt"), content.Length),
            Item(src2, R("dst", "missing.txt"), 10));

        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        await using var db = _harness.CreateContext();
        var job = await db.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.State.Should().Be(JobState.Failed);

        // Sanity: the first item really went through the copy (its partial existed on disk).
        job.Items.Single(i => i.SourceRelativePath == src1).State.Should().Be(JobItemState.Copied);

        // The partial written for the first item must be swept away with the failure.
        PartialsOnDisk().Should().BeEmpty("a Failed job's .fadit-partial files are discardable garbage");
        job.Items.Should().OnlyContain(i => i.TempPath == null,
            "cleaned partials must not leave dangling TempPath pointers for a later retry");

        // The source is never touched by a failure.
        File.Exists(Abs(src1)).Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_a_non_running_job_cleans_its_partials()
    {
        // Layout left by an interrupted run: job checkpointed in Copying, orphan partial on
        // disk, processor not running. The user cancels from the API → partial must go.
        const string content = "half-written data";
        var srcRel = R("src", "doc.txt");
        var dstRel = R("dst", "doc.txt");
        var partialRel = dstRel + ".fadit-partial";

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(partialRel), "half");

        SeedVolume();
        int jobId = SeedJob(JobState.Copying,
            Item(srcRel, dstRel, content.Length, JobItemState.Copying, partialRel));

        await MakeQueue().CancelAsync(jobId, CancellationToken.None);

        await using var db = _harness.CreateContext();
        var job = await db.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.State.Should().Be(JobState.Cancelled);

        PartialsOnDisk().Should().BeEmpty("cancelling must not leave orphan partials on the target");
        job.Items.Should().OnlyContain(i => i.TempPath == null);

        // Cancel never touches the source.
        File.Exists(Abs(srcRel)).Should().BeTrue();
    }
}
