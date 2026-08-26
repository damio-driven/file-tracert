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
/// Step 15a — a Copy executed for real: real <see cref="Win32FileMover"/>, real
/// <see cref="SpaceLedger"/>, real <see cref="JobExecutionEngine"/>, real files under a temp
/// sandbox, real SQLite. Nothing about the state machine is worth asserting against a substitute.
///
/// What is under test is the difference from a Move, and it is exactly two things: the chain stops
/// one step earlier (no <see cref="JobState.DeletingSource"/>, so the source survives), and the
/// destination row goes from promise to fact instead of the source row travelling.
/// </summary>
public sealed class CopyExecutionTests : IDisposable
{
    private const int Vol1Id = 1;
    private const int Vol2Id = 2;

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly Win32FileMover _mover;
    private readonly Win32VolumeProbe _probe;
    private readonly JobCancellationRegistry _cancellation = new();
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public CopyExecutionTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            // Two ledger volumes over ONE physical GUID, so the real mover touches real files.
            setup.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_Volumes_VolumeGuid");
        }

        var services = new ServiceCollection();
        var harness = _harness;
        services.AddScoped<FileTracertDbContext>(_ => harness.CreateContext());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _ledger = new SpaceLedger(scopeFactory, NullLogger<SpaceLedger>.Instance);

        _mover = new Win32FileMover(NullLogger<Win32FileMover>.Instance);
        _probe = new Win32VolumeProbe(
            new WmiPhysicalDiskResolver(NullLogger<WmiPhysicalDiskResolver>.Instance),
            NullLogger<Win32VolumeProbe>.Instance);

        var tempPath = Path.GetTempPath();
        var vol = _probe.EnumerateVolumes()
            .Where(v => v.MountPoints.Count > 0)
            .OrderByDescending(v => v.MountPoints[0].Length)
            .First(v => tempPath.StartsWith(v.MountPoints[0], StringComparison.OrdinalIgnoreCase));

        _volumeGuid = vol.VolumeGuid;
        _mountPoint = vol.MountPoints[0];
        _absRoot = Path.Combine(tempPath, $"ft-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_absRoot);
        _relRoot = Path.GetRelativePath(_mountPoint, _absRoot);

        SeedVolumes();
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_absRoot))
            Directory.Delete(_absRoot, recursive: true);
    }

    private string R(params string[] parts) => Path.Combine([_relRoot, .. parts]);
    private string Abs(string rel) => Path.GetFullPath(Path.Combine(_mountPoint, rel));

    private void SeedVolumes()
    {
        using var db = _harness.CreateContext();
        db.Volumes.AddRange(
            new Volume { Id = Vol1Id, VolumeGuid = _volumeGuid, FileSystem = "NTFS", FreeBytesLastKnown = 1_000_000, IsOnline = true },
            new Volume { Id = Vol2Id, VolumeGuid = _volumeGuid, FileSystem = "NTFS", FreeBytesLastKnown = 1_000_000, IsOnline = true });
        db.Directories.AddRange(
            new DirectoryNode { Id = 1, VolumeId = Vol1Id, Name = "", MaterializedPath = "", IsMaterialized = true, IsPresent = true },
            new DirectoryNode { Id = 2, VolumeId = Vol2Id, Name = "", MaterializedPath = "", IsMaterialized = true, IsPresent = true });
        db.SaveChanges();
    }

    private int SeedFile(int id, int volId, string dirRel, string name, string content)
    {
        Directory.CreateDirectory(Abs(dirRel));
        File.WriteAllText(Abs(Path.Combine(dirRel, name)), content);

        using var db = _harness.CreateContext();
        int dirId = 100 + id;
        db.Directories.Add(new DirectoryNode
        {
            Id = dirId, VolumeId = volId, Name = Path.GetFileName(dirRel),
            MaterializedPath = dirRel, IsMaterialized = true, IsPresent = true,
        });
        db.Files.Add(new FileEntry
        {
            Id = id, VolumeId = volId, DirectoryId = dirId, Name = name, Extension = "bin",
            Category = FileCategory.Other, SizeBytes = content.Length,
            IsPresent = true, IsIncluded = true, IsMaterialized = true,
            FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private QueueService MakeQueue(FileTracertDbContext db) =>
        new(db, _ledger, TestProjection.Space(db, _ledger, _probe), _cancellation, _mover, new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db),
            TestProjection.Unblocker(db),
            TestProjection.Revaluator(db, _ledger, fts: null, _probe),
            TestProjection.Realtime(), NullLogger<QueueService>.Instance);

    private JobExecutionEngine MakeEngine(FileTracertDbContext db) =>
        new(db, _mover, _ledger, TestProjection.Space(db, _ledger, _probe), TestProjection.Index(db),
            TestProjection.Overlay(db),
            new FileTracert.Business.Notifications.NotificationService(db, TestProjection.Realtime()),
            TimeProvider.System, TestProjection.Realtime(), NullLogger<JobExecutionEngine>.Instance);

    private async Task<int> EnqueueAsync(CreateJobRequest req)
    {
        await using var db = _harness.CreateContext();
        var dto = await MakeQueue(db).EnqueueAsync(req, CancellationToken.None);
        return dto.Id;
    }

    private async Task RunAsync(int jobId)
    {
        await using var db = _harness.CreateContext();
        await MakeEngine(db).ExecuteJobAsync(jobId, CancellationToken.None);
    }

    private async Task<OperationJob> LoadAsync(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    // ── the whole thing, end to end ───────────────────────────────────────────

    [Fact]
    public async Task An_intra_volume_copy_writes_the_file_and_leaves_the_source_alone()
    {
        SeedFile(1, Vol1Id, R("src"), "a.bin", "hello world");

        var jobId = await EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile, SourceFileId = 1,
            TargetVolumeId = Vol1Id, TargetRelativePath = R("dst"),
        });

        await RunAsync(jobId);

        var job = await LoadAsync(jobId);
        job.State.Should().Be(JobState.Completed);

        // The bytes are in both places. This is the whole promise of a copy, and the assertion a
        // move could never pass.
        File.Exists(Abs(R("src", "a.bin"))).Should().BeTrue("a copy never touches its source");
        File.ReadAllText(Abs(Path.Combine(R("dst"), "a.bin"))).Should().Be("hello world");

        // No .fadit-partial survives a finished job.
        Directory.GetFiles(Abs(R("dst")), "*.fadit-partial").Should().BeEmpty();
        job.Items.Should().OnlyContain(i => i.State == JobItemState.Done);
        job.Items.Should().OnlyContain(i => i.TempPath == null);
    }

    [Fact]
    public async Task The_state_machine_skips_DeletingSource_and_nothing_reaches_the_recycle_bin()
    {
        SeedFile(1, Vol1Id, R("src"), "a.bin", "0123456789");

        var jobId = await EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile, SourceFileId = 1,
            TargetVolumeId = Vol2Id, TargetRelativePath = R("dst"),
        });

        await RunAsync(jobId);

        var job = await LoadAsync(jobId);
        job.State.Should().Be(JobState.Completed);
        job.BytesProcessed.Should().Be(10);
        // §4's chain minus one step. DeletingSource is the ONLY step that recycles anything, and a
        // copy must never travel it — the source file being here is that assertion's other half.
        File.Exists(Abs(R("src", "a.bin"))).Should().BeTrue();
    }

    [Fact]
    public async Task The_projected_destination_row_becomes_a_real_one()
    {
        SeedFile(1, Vol1Id, R("src"), "a.bin", "hello");

        var jobId = await EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile, SourceFileId = 1,
            TargetVolumeId = Vol2Id, TargetRelativePath = R("dst"),
        });

        int projectedId;
        await using (var before = _harness.CreateContext())
        {
            var row = await before.Files.SingleAsync(f => f.PendingJobId == jobId);
            row.IsMaterialized.Should().BeFalse();
            row.IsPresent.Should().BeFalse();
            projectedId = row.Id;
        }

        await RunAsync(jobId);

        await using var after = _harness.CreateContext();
        var landed = await after.Files.SingleAsync(f => f.Id == projectedId);

        // The SAME row: promoted, not replaced. A second row next to it would be the "ghost
        // directory" bug of §5, one level down.
        landed.IsMaterialized.Should().BeTrue();
        landed.IsPresent.Should().BeTrue();
        landed.PendingState.Should().Be(EntityPendingState.None);
        landed.PendingJobId.Should().BeNull();
        landed.VolumeId.Should().Be(Vol2Id);
        landed.SizeBytes.Should().Be(5);
        // The FRN of a file only a scan can identify. The unique (VolumeId, UsnFileRef) index is
        // filtered, so leaving it null is legal however many copies land.
        landed.UsnFileRef.Should().BeNull();

        // And the source row is untouched — still on its own volume, still materialized.
        var source = await after.Files.SingleAsync(f => f.Id == 1);
        source.VolumeId.Should().Be(Vol1Id);
        source.IsMaterialized.Should().BeTrue();
        source.IsPresent.Should().BeTrue();
        source.PendingState.Should().Be(EntityPendingState.None);
    }

    [Fact]
    public async Task A_folder_copy_reproduces_the_subtree_and_indexes_every_landed_file()
    {
        SeedFile(1, Vol1Id, R("tree"), "top.bin", "aaa");
        SeedFile(2, Vol1Id, R("tree", "deep"), "low.bin", "bbbb");

        // Re-parent the deep directory so the subtree query finds it.
        await using (var fix = _harness.CreateContext())
        {
            var deep = await fix.Directories.SingleAsync(d => d.MaterializedPath == R("tree", "deep"));
            deep.ParentId = (await fix.Directories.SingleAsync(d => d.MaterializedPath == R("tree"))).Id;
            await fix.SaveChangesAsync();
        }

        var jobId = await EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFolder,
            SourceDirectoryId = await DirIdAsync(R("tree")),
            TargetVolumeId = Vol2Id, TargetRelativePath = R("backup"),
        });

        await RunAsync(jobId);

        (await LoadAsync(jobId)).State.Should().Be(JobState.Completed);

        File.ReadAllText(Abs(Path.Combine(R("backup"), "tree", "top.bin"))).Should().Be("aaa");
        File.ReadAllText(Abs(Path.Combine(R("backup"), "tree", "deep", "low.bin"))).Should().Be("bbbb");
        // The originals, all of them, still there.
        File.Exists(Abs(R("tree", "top.bin"))).Should().BeTrue();
        File.Exists(Abs(R("tree", "deep", "low.bin"))).Should().BeTrue();

        await using var db = _harness.CreateContext();
        var landed = await db.Files.Where(f => f.VolumeId == Vol2Id).ToListAsync();
        landed.Should().HaveCount(2);
        landed.Should().OnlyContain(f => f.IsMaterialized && f.IsPresent
                                      && f.PendingState == EntityPendingState.None);
    }

    private async Task<int> DirIdAsync(string path)
    {
        await using var db = _harness.CreateContext();
        return await db.Directories.Where(d => d.MaterializedPath == path).Select(d => d.Id).SingleAsync();
    }

    // ── resume ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_copy_interrupted_in_Copying_resumes_and_finishes()
    {
        SeedFile(1, Vol1Id, R("src"), "a.bin", "resume me");

        var jobId = await EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile, SourceFileId = 1,
            TargetVolumeId = Vol2Id, TargetRelativePath = R("dst"),
        });

        // The footprint of a run killed mid-copy: state Copying, item Copying, an orphan partial.
        Directory.CreateDirectory(Abs(R("dst")));
        var partialRel = Path.Combine(R("dst"), "a.bin.fadit-partial");
        File.WriteAllText(Abs(partialRel), "res");
        await using (var crash = _harness.CreateContext())
        {
            var job = await crash.OperationJobs.Include(j => j.Items).SingleAsync(j => j.Id == jobId);
            job.State = JobState.Copying;
            var item = job.Items.Single(i => i.FileId != null);
            item.State = JobItemState.Copying;
            item.TempPath = partialRel;
            item.BytesCopied = 3;
            await crash.SaveChangesAsync();
        }

        await RunAsync(jobId);

        (await LoadAsync(jobId)).State.Should().Be(JobState.Completed);
        // Re-copied from scratch, not resumed from a half-written partial.
        File.ReadAllText(Abs(Path.Combine(R("dst"), "a.bin"))).Should().Be("resume me");
        File.Exists(Abs(partialRel)).Should().BeFalse();
        File.Exists(Abs(R("src", "a.bin"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_copy_interrupted_AFTER_the_finalize_does_not_copy_anything_again()
    {
        SeedFile(1, Vol1Id, R("src"), "a.bin", "already there");

        var jobId = await EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile, SourceFileId = 1,
            TargetVolumeId = Vol2Id, TargetRelativePath = R("dst"),
        });

        // The footprint of a run killed between FinalizePartial and the Verified checkpoint: the
        // final file exists, the partial does not, the item still reads Copied.
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(Path.Combine(R("dst"), "a.bin")), "already there");
        var stamp = File.GetLastWriteTimeUtc(Abs(Path.Combine(R("dst"), "a.bin")));

        await using (var crash = _harness.CreateContext())
        {
            var job = await crash.OperationJobs.Include(j => j.Items).SingleAsync(j => j.Id == jobId);
            job.State = JobState.Verifying;
            var item = job.Items.Single(i => i.FileId != null);
            item.State = JobItemState.Copied;
            item.TempPath = Path.Combine(R("dst"), "a.bin.fadit-partial");
            await crash.SaveChangesAsync();
        }

        await RunAsync(jobId);

        (await LoadAsync(jobId)).State.Should().Be(JobState.Completed);
        // Verified in place, never rewritten: the file the interrupted run published is the file
        // that is there, byte for byte and stamp for stamp.
        File.GetLastWriteTimeUtc(Abs(Path.Combine(R("dst"), "a.bin"))).Should().Be(stamp);
        File.Exists(Abs(R("src", "a.bin"))).Should().BeTrue();
    }

    // ── the ledger is released, once ──────────────────────────────────────────

    [Fact]
    public async Task Completing_an_intra_volume_copy_releases_its_reservation()
    {
        SeedFile(1, Vol1Id, R("src"), "a.bin", "1234567890");

        var jobId = await EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile, SourceFileId = 1,
            TargetVolumeId = Vol1Id, TargetRelativePath = R("dst"),
        });

        await using (var db = _harness.CreateContext())
        {
            // The reservation an intra-volume operation could not have before step 15a.
            (await db.SpaceLedgerEntries.CountAsync(e => e.JobId == jobId && e.IsActive)).Should().Be(1);
        }

        await RunAsync(jobId);

        await using var after = _harness.CreateContext();
        (await after.SpaceLedgerEntries.CountAsync(e => e.JobId == jobId && e.IsActive)).Should().Be(0);
    }
}
