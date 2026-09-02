using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Platform;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Step 15b — the last hole in §4's «mai copiare sulla fiducia di una stima».
///
/// The hard space re-check lived inside <c>if (job.State == JobState.Pending)</c>, so a job that
/// resumed from a checkpoint — after a crash, a shutdown, or a block that was later released —
/// walked straight back into the copy without asking the drive anything. Everything the check
/// exists to prevent (another process filling the target while the job sat in the queue) applies
/// exactly as much to the second attempt as to the first.
///
/// <para>The refinement that makes the fix a fix rather than a worse defect: on resume, part of
/// the demand is ALREADY on the target. Re-asking for the whole of it would double-count and park
/// every large interrupted job for ever — 9 GB of a 10 GB copy written, half a gigabyte free, and
/// a job that needs 1 GB more gets refused for wanting 10.</para>
///
/// Real engine, real ledger, real <see cref="Win32FileMover"/>, real files; only the free-space
/// probe is a stub, because "the drive filled up between two attempts" is not something a test can
/// arrange on a real disk.
/// </summary>
public sealed class ResumedJobSpaceRecheckTests : IDisposable
{
    private const int SrcVolId = 1;
    private const int TgtVolId = 2;

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly Win32FileMover _mover;
    private readonly JobCancellationRegistry _cancellation = new();
    private readonly StubFreeSpaceProbe _probe = new(freeBytes: 1_000_000);
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public ResumedJobSpaceRecheckTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            setup.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_Volumes_VolumeGuid");
        }

        var services = new ServiceCollection();
        var harness = _harness;
        services.AddScoped<FileTracertDbContext>(_ => harness.CreateContext());
        _ledger = new SpaceLedger(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SpaceLedger>.Instance);

        _mover = new Win32FileMover(NullLogger<Win32FileMover>.Instance);

        var realProbe = new Win32VolumeProbe(
            new WmiPhysicalDiskResolver(NullLogger<WmiPhysicalDiskResolver>.Instance),
            NullLogger<Win32VolumeProbe>.Instance);
        var tempPath = Path.GetTempPath();
        var vol = realProbe.EnumerateVolumes()
            .Where(v => v.MountPoints.Count > 0)
            .OrderByDescending(v => v.MountPoints[0].Length)
            .First(v => tempPath.StartsWith(v.MountPoints[0], StringComparison.OrdinalIgnoreCase));

        _volumeGuid = vol.VolumeGuid;
        _mountPoint = vol.MountPoints[0];
        _absRoot = Path.Combine(tempPath, $"ft-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_absRoot);
        _relRoot = Path.GetRelativePath(_mountPoint, _absRoot);

        Seed();
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_absRoot))
            Directory.Delete(_absRoot, recursive: true);
    }

    private string R(params string[] parts) => Path.Combine([_relRoot, .. parts]);
    private string Abs(string rel) => Path.GetFullPath(Path.Combine(_mountPoint, rel));

    private void Seed()
    {
        using var db = _harness.CreateContext();
        db.Volumes.AddRange(
            new Volume { Id = SrcVolId, VolumeGuid = _volumeGuid, FileSystem = "NTFS", FreeBytesLastKnown = 1_000_000, IsOnline = true },
            new Volume { Id = TgtVolId, VolumeGuid = _volumeGuid, FileSystem = "NTFS", FreeBytesLastKnown = 1_000_000, IsOnline = true });
        db.Directories.AddRange(
            new DirectoryNode { Id = 1, VolumeId = SrcVolId, Name = "", MaterializedPath = "", IsMaterialized = true, IsPresent = true },
            new DirectoryNode { Id = 2, VolumeId = TgtVolId, Name = "", MaterializedPath = "", IsMaterialized = true, IsPresent = true });
        db.SaveChanges();
    }

    /// <summary>Seeds one real file and its catalog row, and returns the file id.</summary>
    private int SeedFile(int id, string dirRel, string name, int bytes)
    {
        Directory.CreateDirectory(Abs(dirRel));
        File.WriteAllBytes(Abs(Path.Combine(dirRel, name)), new byte[bytes]);

        using var db = _harness.CreateContext();
        int dirId = 100 + id;
        db.Directories.Add(new DirectoryNode
        {
            Id = dirId, VolumeId = SrcVolId, Name = Path.GetFileName(dirRel),
            MaterializedPath = dirRel, IsMaterialized = true, IsPresent = true,
        });
        db.Files.Add(new FileEntry
        {
            Id = id, VolumeId = SrcVolId, DirectoryId = dirId, Name = name, Extension = "bin",
            Category = FileCategory.Other, SizeBytes = bytes,
            IsPresent = true, IsIncluded = true, IsMaterialized = true,
            FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private QueueService Queue(FileTracertDbContext db) =>
        new(db, _ledger, TestProjection.Space(db, _ledger, _probe), _cancellation, _mover, new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db), TestProjection.Unblocker(db),
            TestProjection.Revaluator(db, _ledger, fts: null, _probe),
            TestProjection.Realtime(), NullLogger<QueueService>.Instance);

    private JobExecutionEngine Engine(FileTracertDbContext db) =>
        new(db, _mover, _ledger, TestProjection.Space(db, _ledger, _probe), TestProjection.Index(db),
            TestProjection.Overlay(db),
            new FileTracert.Business.Notifications.NotificationService(db, TestProjection.Realtime()),
            TimeProvider.System, TestProjection.Realtime(), NullLogger<JobExecutionEngine>.Instance);

    private async Task<int> EnqueueMoveAsync(int fileId)
    {
        await using var db = _harness.CreateContext();
        var dto = await Queue(db).EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = fileId,
            TargetVolumeId = TgtVolId, TargetRelativePath = R("dst"),
        }, CancellationToken.None);
        return dto.Id;
    }

    private async Task RunAsync(int jobId)
    {
        await using var db = _harness.CreateContext();
        await Engine(db).ExecuteJobAsync(jobId, CancellationToken.None);
    }

    private async Task<OperationJob> LoadAsync(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    /// <summary>Parks the job exactly where an interrupted run leaves it: mid-copy, at a checkpoint.</summary>
    private async Task ParkInCopyingAsync(int jobId)
    {
        await using var db = _harness.CreateContext();
        var job = await db.OperationJobs.Include(j => j.Items).SingleAsync(j => j.Id == jobId);
        job.State = JobState.Copying;
        job.StartedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // ── the defect ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_job_resumed_mid_copy_asks_the_drive_again_before_writing_more()
    {
        SeedFile(1, R("src"), "payload.bin", 4_000);
        var jobId = await EnqueueMoveAsync(1);
        await ParkInCopyingAsync(jobId);

        // Another process ate the drive while the job sat at its checkpoint. This is exactly the
        // situation the Pending-branch check exists for; nothing about it is less true the second
        // time round.
        _probe.FreeBytes = 10;

        await RunAsync(jobId);

        var job = await LoadAsync(jobId);
        job.State.Should().Be(JobState.Blocked,
            "a resumed job must re-ask the drive, not resume on the strength of an old answer");
        job.BlockReason.Should().Be(JobBlockReason.InsufficientSpace);

        // And nothing was written: the point is not the label on the job, it is the bytes.
        File.Exists(Abs(Path.Combine(R("dst"), "payload.bin"))).Should().BeFalse();
        Directory.Exists(Abs(R("dst")))
            .Should().BeFalse("the copy must not have started at all");
        // §4 — recoverable, so the source is untouched and the job can run later.
        File.Exists(Abs(R("src", "payload.bin"))).Should().BeTrue();
    }

    /// <summary>
    /// The other direction, and the reason the check cannot simply re-ask for the whole demand:
    /// most of the bytes are already on the target, so what is left is what must fit. Without
    /// this, the fix above would park every interrupted large job for ever.
    /// </summary>
    [Fact]
    public async Task A_resumed_job_only_needs_room_for_what_is_LEFT()
    {
        // Two files of 4 000 bytes: 8 000 demanded in total.
        SeedFile(1, R("src"), "a.bin", 4_000);
        SeedFile(2, R("src"), "b.bin", 4_000);

        await using (var db = _harness.CreateContext())
        {
            var dto = await Queue(db).EnqueueAsync(new CreateJobRequest
            {
                Type = JobType.MoveFolder, SourceDirectoryId = 101,
                TargetVolumeId = TgtVolId, TargetRelativePath = R("dst"),
            }, CancellationToken.None);

            // Both files sit under directory 101; the second was seeded under 102, so re-point it.
            var f2 = await db.Files.SingleAsync(f => f.Id == 2);
            f2.DirectoryId = 101;
            await db.SaveChangesAsync();
            _ = dto;
        }

        var jobId = (await LoadAsync((await FirstJobIdAsync()))).Id;

        // The footprint of a run interrupted after the FIRST file landed.
        await using (var db = _harness.CreateContext())
        {
            var parked = await db.OperationJobs.Include(j => j.Items).SingleAsync(j => j.Id == jobId);
            parked.State = JobState.Copying;
            var first = parked.Items.First(i => i.FileId == 1);
            first.State = JobItemState.Copied;
            first.BytesCopied = first.SizeBytes;
            await db.SaveChangesAsync();
        }

        // Room for the remaining file and its margin, but NOT for the whole original demand.
        _probe.FreeBytes = 5_000;

        await RunAsync(jobId);

        var job = await LoadAsync(jobId);
        job.State.Should().NotBe(JobState.Blocked,
            "the job needs room for the 4 000 bytes still to write, not for the 8 000 it started with");
    }

    private async Task<int> FirstJobIdAsync()
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.OrderBy(j => j.Id).Select(j => j.Id).FirstAsync();
    }

    /// <summary>
    /// A job resumed at Verifying has already written every byte it is going to write. Re-checking
    /// there would refuse a job whose remaining steps only ever FREE space.
    /// </summary>
    [Fact]
    public async Task A_job_resumed_at_Verifying_is_not_refused_for_space()
    {
        SeedFile(1, R("src"), "payload.bin", 4_000);
        var jobId = await EnqueueMoveAsync(1);

        // Let it copy for real, then rewind the job to the checkpoint an interrupted run leaves
        // just before verification.
        await RunAsync(jobId);
        (await LoadAsync(jobId)).State.Should().Be(JobState.Completed, "arrange: the first run must succeed");

        _probe.FreeBytes = 0;

        await using (var db = _harness.CreateContext())
        {
            var job = await db.OperationJobs.Include(j => j.Items).SingleAsync(j => j.Id == jobId);
            job.State = JobState.Verifying;
            job.CompletedUtc = null;
            foreach (var item in job.Items) item.State = JobItemState.Copied;
            await db.SaveChangesAsync();
        }

        await RunAsync(jobId);

        (await LoadAsync(jobId)).State.Should().NotBe(JobState.Blocked,
            "the bytes are already written; what is left frees space, it does not consume it");
    }

    /// <summary>
    /// The number the Coda screen shows for a job parked mid-copy must be the number that parked
    /// it. The list deliberately does not load the job's items (step 11e, E1), so the
    /// outstanding-bytes derivation would fall back to the whole original demand and quote a
    /// deficit the engine never decided on — a job 9 GB into a 10 GB copy shown as short of the
    /// full 10 GB. Found by the final code review of this step.
    /// </summary>
    [Fact]
    public async Task The_queue_list_quotes_the_deficit_that_actually_parked_the_job()
    {
        SeedFile(1, R("src"), "a.bin", 4_000);
        SeedFile(2, R("src"), "b.bin", 4_000);

        int jobId;
        await using (var db = _harness.CreateContext())
        {
            var dto = await Queue(db).EnqueueAsync(new CreateJobRequest
            {
                Type = JobType.MoveFolder, SourceDirectoryId = 101,
                TargetVolumeId = TgtVolId, TargetRelativePath = R("dst"),
            }, CancellationToken.None);
            jobId = dto.Id;

            var f2 = await db.Files.SingleAsync(f => f.Id == 2);
            f2.DirectoryId = 101;
            await db.SaveChangesAsync();
        }

        // Half the demand already on the target, and the drive now too full for the rest.
        await using (var db = _harness.CreateContext())
        {
            var parked = await db.OperationJobs.Include(j => j.Items).SingleAsync(j => j.Id == jobId);
            parked.State = JobState.Copying;
            var first = parked.Items.First(i => i.FileId == 1);
            first.State = JobItemState.Copied;
            first.BytesCopied = first.SizeBytes;
            await db.SaveChangesAsync();
        }

        _probe.FreeBytes = 10;
        await RunAsync(jobId);

        var blocked = await LoadAsync(jobId);
        blocked.State.Should().Be(JobState.Blocked, "arrange: the resume must have been refused");

        await using var listDb = _harness.CreateContext();
        var page = await Queue(listDb).ListAsync(0, 50, CancellationToken.None);
        var row = page.Items.Single(j => j.Id == jobId);

        // 4 000 still to write, not the 8 000 the job started with.
        row.Feasibility.Should().NotBeNull();
        row.Feasibility!.RequiredBytes.Should().Be(4_000,
            "the screen must quote what is left, which is what the engine judged");
    }
}
