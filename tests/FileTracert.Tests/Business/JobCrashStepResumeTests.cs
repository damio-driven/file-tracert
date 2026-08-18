using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data.Entities;
using FileTracert.Platform;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// Review finding #4: three steps of the state machine were not idempotent at crash.
/// Each test arranges the exact on-disk + DB layout a crash leaves behind and expects the
/// resumed engine to finish the job cleanly instead of failing against its own prior work.
/// Real <see cref="Win32FileMover"/> + real SQLite — the resume behavior is what's under test.
/// </summary>
[Trait("Category", "Platform")]
public sealed class JobCrashStepResumeTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;
    private readonly Win32FileMover _mover;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public JobCrashStepResumeTests()
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
        _absRoot = Path.Combine(tempPath, $"ft-stepresume-{Guid.NewGuid():N}");
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

        var db = _harness.CreateContext();
        var indexUpdater = TestProjection.Index(db);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db, TestProjection.Realtime());
        return new JobExecutionEngine(db, _mover, ledger, indexUpdater, TestProjection.Overlay(db), notifications,
            TimeProvider.System, TestProjection.Realtime(), NullLogger<JobExecutionEngine>.Instance);
    }

    private void AddVolume(FileTracert.Data.FileTracertDbContext db) =>
        db.Volumes.Add(new Volume
        {
            Id = 1, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
            FreeBytesLastKnown = 1_000_000, IsOnline = true,
        });

    [Fact]
    public async Task Verifying_resume_accepts_already_finalized_item_instead_of_failing_on_missing_partial()
    {
        // Crash window: FinalizePartial renamed partial→final, then the process died before
        // SaveChanges. DB says Copied+TempPath, disk says final-exists+partial-absent.
        const string content = "finalized before the crash";
        var srcRel = R("src", "doc.txt");
        var dstRel = R("dst", "doc.txt");
        var partialRel = dstRel + ".fadit-partial";

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(dstRel), content); // the renamed partial — already final
        // no partial on disk

        int jobId;
        using (var db = _harness.CreateContext())
        {
            AddVolume(db);
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
                SizeBytes = content.Length, State = JobItemState.Copied,
                TempPath = partialRel, BytesCopied = content.Length,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        using var check = _harness.CreateContext();
        var final = await check.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
        final.State.Should().Be(JobState.Completed,
            $"an already-finalized item is progress, not an error (error='{final.ErrorMessage}')");
        final.Items.Single().State.Should().Be(JobItemState.Done);

        File.ReadAllText(Abs(dstRel)).Should().Be(content);
        File.Exists(Abs(srcRel)).Should().BeFalse("the resumed move must still delete the source");
    }

    [Fact]
    public async Task DeletingSource_resume_tolerates_sources_already_recycled_by_the_interrupted_run()
    {
        // Crash window: MoveFolder recycled item1's source, died before the post-loop
        // SaveChanges. DB still says Verified for both; item1's source is gone from disk.
        const string content1 = "first file";
        const string content2 = "second file";
        var src1 = R("src", "one.txt");
        var src2 = R("src", "two.txt");
        var dst1 = R("dst", "one.txt");
        var dst2 = R("dst", "two.txt");

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(src2), content2);   // src1 already recycled pre-crash
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(dst1), content1);
        File.WriteAllText(Abs(dst2), content2);

        int jobId;
        using (var db = _harness.CreateContext())
        {
            AddVolume(db);
            var job = new OperationJob
            {
                Id = 1, Type = JobType.MoveFolder, State = JobState.DeletingSource,
                IsIntraVolume = false, SourceVolumeId = 1, TargetVolumeId = 1,
                TargetRelativePath = R("dst"),
                TotalBytes = content1.Length + content2.Length,
                RequiredBytesTarget = content1.Length + content2.Length,
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            };
            job.Items.Add(new OperationJobItem
            {
                SourceRelativePath = src1, TargetRelativePath = dst1,
                SizeBytes = content1.Length, State = JobItemState.Verified,
                BytesCopied = content1.Length,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            job.Items.Add(new OperationJobItem
            {
                SourceRelativePath = src2, TargetRelativePath = dst2,
                SizeBytes = content2.Length, State = JobItemState.Verified,
                BytesCopied = content2.Length,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        using var check = _harness.CreateContext();
        var final = await check.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
        final.State.Should().Be(JobState.Completed,
            $"a source already recycled by the interrupted run means that item is done (error='{final.ErrorMessage}')");
        final.Items.Should().OnlyContain(i => i.State == JobItemState.Done);

        File.Exists(Abs(src2)).Should().BeFalse("the resumed run must still recycle the remaining source");
        File.ReadAllText(Abs(dst1)).Should().Be(content1);
        File.ReadAllText(Abs(dst2)).Should().Be(content2);
    }

    [Fact]
    public async Task Simple_intra_volume_move_resume_detects_already_applied_and_completes_with_index_update()
    {
        // Crash window: File.Move ran, the process died before any checkpoint. The job is
        // still Pending; a re-run used to throw FileNotFoundException and fail a move that
        // physically succeeded — and the index update never ran.
        const string content = "moved before the crash";
        var srcRel = R("from", "pic.jpg");
        var dstRel = R("to", "pic.jpg");

        Directory.CreateDirectory(Abs(R("from")));
        Directory.CreateDirectory(Abs(R("to")));
        File.WriteAllText(Abs(dstRel), content); // the move already happened
        // source gone

        int jobId;
        using (var db = _harness.CreateContext())
        {
            AddVolume(db);
            db.Directories.Add(new DirectoryNode
            {
                Id = 1, VolumeId = 1, Name = "from", MaterializedPath = R("from"),
                IsMaterialized = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.Files.Add(new FileEntry
            {
                Id = 10, VolumeId = 1, DirectoryId = 1, Name = "pic.jpg", Extension = ".jpg",
                SizeBytes = content.Length, IsIncluded = true, IsPresent = true,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
            });
            var job = new OperationJob
            {
                Id = 1, Type = JobType.MoveFile, State = JobState.Pending,
                IsIntraVolume = true, SourceVolumeId = 1, TargetVolumeId = 1,
                TargetRelativePath = dstRel,
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                StartedUtc = DateTime.UtcNow, // the interrupted run had started
            };
            job.Items.Add(new OperationJobItem
            {
                FileId = 10,
                SourceRelativePath = srcRel, TargetRelativePath = dstRel,
                SizeBytes = content.Length, State = JobItemState.Pending,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        using var check = _harness.CreateContext();
        var final = await check.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
        final.State.Should().Be(JobState.Completed,
            $"target-exists + source-absent means the op already succeeded (error='{final.ErrorMessage}')");
        final.Items.Single().State.Should().Be(JobItemState.Done);

        // The index update must have run: the file row now points at the target directory.
        var file = await check.Files.AsNoTracking().SingleAsync(f => f.Id == 10);
        var newDir = await check.Directories.AsNoTracking().SingleAsync(d => d.Id == file.DirectoryId);
        newDir.MaterializedPath.Should().Be(R("to"));

        File.ReadAllText(Abs(dstRel)).Should().Be(content);
    }
}
