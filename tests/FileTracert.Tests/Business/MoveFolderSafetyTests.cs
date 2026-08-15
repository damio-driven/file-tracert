using FileTracert.Business.Notifications;
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
/// WP3 data-safety tests for cross-volume MoveFolder, run against the REAL
/// <see cref="Win32FileMover"/> on temp directories plus the real QueueService,
/// JobExecutionEngine, SpaceLedger and SQLite DB — no mocks of the components
/// under test. Source and target live in two sandbox subfolders of the same
/// physical volume; the two DB Volume rows share that volume's GUID while the
/// job is marked cross-volume, which drives the full copy/verify/delete pipeline.
/// Tag Category=Platform so CI can filter: <c>dotnet test --filter Category!=Platform</c>.
/// </summary>
[Trait("Category", "Platform")]
public sealed class MoveFolderSafetyTests : IDisposable
{
    private const int SrcVolId = 1;
    private const int TgtVolId = 2;

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly Win32FileMover _mover;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public MoveFolderSafetyTests()
    {
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

        _absRoot = Path.Combine(tempPath, $"ft-wp3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_absRoot);
        _relRoot = Path.GetRelativePath(_mountPoint, _absRoot);

        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            // Both rows point at the same physical volume; the second uses a case-flipped
            // GUID so the unique index passes while Win32 (case-insensitive) still resolves it.
            setup.Volumes.AddRange(
                new Volume
                {
                    Id = SrcVolId, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
                    FreeBytesLastKnown = 1_000_000_000, IsOnline = true,
                },
                new Volume
                {
                    Id = TgtVolId, VolumeGuid = CaseFlipGuid(_volumeGuid), FileSystem = "NTFS",
                    FreeBytesLastKnown = 1_000_000_000, IsOnline = true,
                });
            setup.SaveChanges();
        }
        _ledger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_absRoot))
            Directory.Delete(_absRoot, recursive: true);
        _harness.Dispose();
    }

    // ── harness helpers ───────────────────────────────────────────────────────

    private static IServiceScopeFactory CreateScopeFactory(SqliteInMemoryContext h)
    {
        var services = new ServiceCollection();
        services.AddScoped<FileTracertDbContext>(_ => h.CreateContext());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Volume-relative path inside the sandbox.</summary>
    private string R(params string[] parts) => Path.Combine([_relRoot, .. parts]);

    private string Abs(string rel) => Path.GetFullPath(Path.Combine(_mountPoint, rel));

    private void WriteFile(string relPath, string content = "hello")
    {
        var abs = Abs(relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private int SeedDirectory(string relPath, int? parentId)
    {
        Directory.CreateDirectory(Abs(relPath));
        using var db = _harness.CreateContext();
        var dir = new DirectoryNode
        {
            VolumeId = SrcVolId, ParentId = parentId,
            Name = ScanPathName(relPath), MaterializedPath = relPath, IsMaterialized = true,
        };
        db.Directories.Add(dir);
        db.SaveChanges();
        return dir.Id;
    }

    private int SeedFile(string dirRelPath, int dirId, string name, string content, bool included)
    {
        WriteFile(Path.Combine(dirRelPath, name), content);
        using var db = _harness.CreateContext();
        var file = new FileEntry
        {
            VolumeId = SrcVolId, DirectoryId = dirId,
            Name = name, Extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant(),
            Category = FileCategory.Other, SizeBytes = content.Length,
            IsPresent = true, IsIncluded = included,
            FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
            LastIndexedUtc = DateTime.UtcNow,
        };
        db.Files.Add(file);
        db.SaveChanges();
        return file.Id;
    }

    /// <summary>Flips the case of the hex digits inside the braces only — the literal
    /// <c>\\?\Volume{…}\</c> prefix must stay exact or Win32 refuses the path.</summary>
    private static string CaseFlipGuid(string volumeGuid)
    {
        var open = volumeGuid.IndexOf('{');
        var close = volumeGuid.IndexOf('}');
        return volumeGuid[..(open + 1)]
             + volumeGuid[(open + 1)..close].ToUpperInvariant()
             + volumeGuid[close..];
    }

    private static string ScanPathName(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? path : path[(i + 1)..];
    }

    private QueueService Queue()
    {
        var db = _harness.CreateContext();
        return new QueueService(db, _ledger, new JobCancellationRegistry(),
            _mover, new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db),
            TestProjection.Guard(db), TestProjection.Unblocker(db),
            TestProjection.Revaluator(db, _ledger),
            NullLogger<QueueService>.Instance);
    }

    private JobExecutionEngine Engine()
    {
        var db = _harness.CreateContext();
        return new JobExecutionEngine(
            db, _mover, _ledger,
            TestProjection.Index(db), TestProjection.Overlay(db),
            new NotificationService(db),
            TimeProvider.System, NullLogger<JobExecutionEngine>.Instance);
    }

    private async Task<JobState> ReadState(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync();
    }

    private async Task AssertCompleted(int jobId)
    {
        await using var db = _harness.CreateContext();
        var job = await db.OperationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.State.Should().Be(JobState.Completed, "job error: {0}", job.ErrorMessage ?? "<none>");
    }

    private static CancellationToken None => CancellationToken.None;

    // ── FIX #1 — delete must never touch files that were not copied+verified ──

    [Fact]
    public async Task MoveFolder_cross_volume_keeps_uncopied_files_and_their_directories_on_source()
    {
        // Source folder "Foto": one indexed+included photo, one stray sidecar the
        // scanner never indexed, one excluded file in a subfolder.
        var fotoId = SeedDirectory(R("src", "Foto"), parentId: null);
        var subId = SeedDirectory(R("src", "Foto", "Sub"), parentId: fotoId);
        SeedFile(R("src", "Foto"), fotoId, "a.jpg", "photo-bytes", included: true);
        WriteFile(R("src", "Foto", "sidecar.txt"), "not-indexed");           // on disk only
        SeedFile(R("src", "Foto", "Sub"), subId, "b.xmp", "excluded", included: false);

        var dto = await Queue().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = fotoId,
            TargetVolumeId = TgtVolId,
            TargetRelativePath = R("dst"),
        }, None);

        await Engine().ExecuteJobAsync(dto.Id, None);

        await AssertCompleted(dto.Id);

        // The copied+verified file moved: present on target, gone from source.
        File.Exists(Abs(R("dst", "Foto", "a.jpg"))).Should().BeTrue("the included file must be moved");
        File.ReadAllText(Abs(R("dst", "Foto", "a.jpg"))).Should().Be("photo-bytes");
        File.Exists(Abs(R("src", "Foto", "a.jpg"))).Should().BeFalse("the copied+verified source must be recycled");

        // DATA-LOSS GUARD: everything that was never copied MUST survive on the source.
        File.Exists(Abs(R("src", "Foto", "sidecar.txt"))).Should().BeTrue(
            "a file the index never saw was never copied — deleting it is data loss");
        File.Exists(Abs(R("src", "Foto", "Sub", "b.xmp"))).Should().BeTrue(
            "an excluded file was never copied — deleting it is data loss");
        Directory.Exists(Abs(R("src", "Foto"))).Should().BeTrue("a non-empty directory must not be recycled");
        Directory.Exists(Abs(R("src", "Foto", "Sub"))).Should().BeTrue("a non-empty directory must not be recycled");

        // The incompleteness is surfaced, not silent (§9).
        await using var db = _harness.CreateContext();
        var notification = await db.Notifications.SingleAsync();
        notification.Severity.Should().Be(NotificationSeverity.Warning);
    }

    // ── C21 — empty / all-excluded folders must not "complete" without a syscall ──

    [Fact]
    public async Task MoveFolder_of_empty_folder_creates_the_target_folder_and_removes_the_source()
    {
        // "Sposta la cartella" means the FOLDER moves, even when it holds no indexed file.
        var emptyId = SeedDirectory(R("src", "Empty"), parentId: null);

        var dto = await Queue().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = emptyId,
            TargetVolumeId = TgtVolId,
            TargetRelativePath = R("dst"),
        }, None);

        await Engine().ExecuteJobAsync(dto.Id, None);

        await AssertCompleted(dto.Id);
        Directory.Exists(Abs(R("dst", "Empty"))).Should().BeTrue(
            "an empty folder move must still create the destination folder");
        Directory.Exists(Abs(R("src", "Empty"))).Should().BeFalse(
            "the emptied source folder must be recycled");
    }

    [Fact]
    public async Task MoveFolder_of_all_excluded_folder_creates_target_keeps_source_content_and_warns()
    {
        // Folder whose only file is excluded from the index: nothing is copied, so nothing
        // may be deleted — but the job must do real work (create the target), not lie Completed.
        var dirId = SeedDirectory(R("src", "Raw"), parentId: null);
        SeedFile(R("src", "Raw"), dirId, "shot.raw", "raw-bytes", included: false);

        var dto = await Queue().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = dirId,
            TargetVolumeId = TgtVolId,
            TargetRelativePath = R("dst"),
        }, None);

        await Engine().ExecuteJobAsync(dto.Id, None);

        await AssertCompleted(dto.Id);
        Directory.Exists(Abs(R("dst", "Raw"))).Should().BeTrue("the destination folder must exist");
        File.Exists(Abs(R("src", "Raw", "shot.raw"))).Should().BeTrue(
            "the excluded file was never copied — it must survive on the source");

        await using var db = _harness.CreateContext();
        var notification = await db.Notifications.SingleAsync();
        notification.Severity.Should().Be(NotificationSeverity.Warning);
    }

    [Fact]
    public async Task MoveFolder_recreates_empty_subdirectories_on_the_target()
    {
        // The directory structure is part of the user's data: an empty subfolder of the
        // moved tree must exist on the target after the move, not evaporate.
        var rootId = SeedDirectory(R("src", "Proj"), parentId: null);
        SeedDirectory(R("src", "Proj", "EmptySub"), parentId: rootId);
        SeedFile(R("src", "Proj"), rootId, "readme.txt", "docs", included: true);

        var dto = await Queue().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = rootId,
            TargetVolumeId = TgtVolId,
            TargetRelativePath = R("dst"),
        }, None);

        await Engine().ExecuteJobAsync(dto.Id, None);

        await AssertCompleted(dto.Id);
        File.Exists(Abs(R("dst", "Proj", "readme.txt"))).Should().BeTrue();
        Directory.Exists(Abs(R("dst", "Proj", "EmptySub"))).Should().BeTrue(
            "empty subdirectories are part of the moved folder");
        Directory.Exists(Abs(R("src", "Proj"))).Should().BeFalse("fully-moved source tree must be recycled");
    }

    // ── #15 — no ghost Directories subtree after a cross-volume MoveFolder ────

    [Fact]
    public async Task MoveFolder_cross_volume_drops_the_source_directory_rows_and_materializes_the_target()
    {
        var rootId = SeedDirectory(R("src", "Media"), parentId: null);
        var subId = SeedDirectory(R("src", "Media", "2024"), parentId: rootId);
        SeedFile(R("src", "Media", "2024"), subId, "pic.jpg", "img", included: true);

        var dto = await Queue().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = rootId,
            TargetVolumeId = TgtVolId,
            TargetRelativePath = R("dst"),
        }, None);

        await Engine().ExecuteJobAsync(dto.Id, None);
        await AssertCompleted(dto.Id);

        await using var db = _harness.CreateContext();

        // The physically recycled source subtree must not stay navigable in the Catalog:
        // its rows are de-materialized (the Catalog tree filters on IsMaterialized).
        var srcRoot = await db.Directories.AsNoTracking().SingleAsync(d => d.Id == rootId);
        var srcSub = await db.Directories.AsNoTracking().SingleAsync(d => d.Id == subId);
        srcRoot.IsMaterialized.Should().BeFalse("the source folder was recycled — a ghost tree must not be navigable");
        srcSub.IsMaterialized.Should().BeFalse();

        // The target tree is materialized and holds the moved file.
        var tgtRoot = await db.Directories.AsNoTracking()
            .SingleAsync(d => d.VolumeId == TgtVolId && d.MaterializedPath == R("dst", "Media"));
        tgtRoot.IsMaterialized.Should().BeTrue();
        var tgtSub = await db.Directories.AsNoTracking()
            .SingleAsync(d => d.VolumeId == TgtVolId && d.MaterializedPath == R("dst", "Media", "2024"));
        tgtSub.IsMaterialized.Should().BeTrue();

        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Name == "pic.jpg");
        file.VolumeId.Should().Be(TgtVolId);
        file.DirectoryId.Should().Be(tgtSub.Id);
    }

    [Fact]
    public async Task MoveFolder_keeps_source_directory_rows_materialized_when_content_was_left_behind()
    {
        // A directory kept on disk because it still holds uncopied content must STAY
        // navigable in the Catalog — de-materializing it would hide real files.
        var dirId = SeedDirectory(R("src", "Mixed"), parentId: null);
        SeedFile(R("src", "Mixed"), dirId, "in.jpg", "copied", included: true);
        WriteFile(R("src", "Mixed", "stray.txt"), "left behind");

        var dto = await Queue().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = dirId,
            TargetVolumeId = TgtVolId,
            TargetRelativePath = R("dst"),
        }, None);

        await Engine().ExecuteJobAsync(dto.Id, None);
        await AssertCompleted(dto.Id);

        await using var db = _harness.CreateContext();
        var srcDir = await db.Directories.AsNoTracking().SingleAsync(d => d.Id == dirId);
        srcDir.IsMaterialized.Should().BeTrue("the directory physically survives with leftover content");
    }
}
