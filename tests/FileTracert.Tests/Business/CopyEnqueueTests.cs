using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Step 15a — enqueueing a Copy.
///
/// The point of these tests is the one thing a Copy does that no other operation does:
/// <b>an intra-volume Copy consumes space</b>. Every other operation that stays on one volume is
/// metadata (§5), so the queue could treat <c>IsIntraVolume</c> as a synonym for "free". A copy
/// writes a second set of bytes onto the very volume it reads from, and the two directions are
/// asserted separately: an intra-volume MOVE must still demand nothing, an intra-volume COPY must
/// demand its full size.
/// </summary>
public sealed class CopyEnqueueTests : IDisposable
{
    private const int Vol1Id = 1;   // 10 000 bytes free, online
    private const int Vol2Id = 2;   //  5 000 bytes free, online
    private const int RootId = 1;   // volume root of Vol1
    private const int DocsId = 2;   // "Docs"        on Vol1
    private const int SubId = 3;    // "Docs\Sub"    on Vol1
    private const int Vol2RootId = 4;
    private const int File1Id = 1;  // "report.txt"  1 000 bytes in Docs
    private const int File2Id = 2;  // "data.csv"    2 000 bytes in Docs\Sub

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly JobCancellationRegistry _cancellation = new();

    public CopyEnqueueTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        _ledger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
        Seed();
    }

    public void Dispose() => _harness.Dispose();

    private static IServiceScopeFactory CreateScopeFactory(SqliteInMemoryContext h)
    {
        var services = new ServiceCollection();
        services.AddScoped<FileTracertDbContext>(_ => h.CreateContext());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private QueueService Svc()
    {
        var db = _harness.CreateContext();
        return new QueueService(db, _ledger, TestProjection.Space(db, _ledger), _cancellation,
            NSubstitute.Substitute.For<FileTracert.Contracts.Platform.IFileMover>(),
            new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db),
            TestProjection.Unblocker(db),
            TestProjection.Revaluator(db, _ledger),
            TestProjection.Realtime(), NullLogger<QueueService>.Instance);
    }

    private void Seed()
    {
        using var db = _harness.CreateContext();

        db.Volumes.AddRange(
            new Volume
            {
                Id = Vol1Id, VolumeGuid = @"\\?\Volume{aaa-1}\",
                FileSystem = "NTFS", FreeBytesLastKnown = 10_000, IsOnline = true
            },
            new Volume
            {
                Id = Vol2Id, VolumeGuid = @"\\?\Volume{bbb-2}\",
                FileSystem = "NTFS", FreeBytesLastKnown = 5_000, IsOnline = true
            });

        db.Directories.AddRange(
            new DirectoryNode
            {
                Id = RootId, VolumeId = Vol1Id, Name = "",
                MaterializedPath = "", IsMaterialized = true, IsPresent = true
            },
            new DirectoryNode
            {
                Id = DocsId, VolumeId = Vol1Id, ParentId = RootId, Name = "Docs",
                MaterializedPath = "Docs", IsMaterialized = true, IsPresent = true
            },
            new DirectoryNode
            {
                Id = SubId, VolumeId = Vol1Id, ParentId = DocsId, Name = "Sub",
                MaterializedPath = @"Docs\Sub", IsMaterialized = true, IsPresent = true
            },
            new DirectoryNode
            {
                Id = Vol2RootId, VolumeId = Vol2Id, Name = "",
                MaterializedPath = "", IsMaterialized = true, IsPresent = true
            });

        db.Files.AddRange(
            NewFile(File1Id, DocsId, "report.txt", 1_000),
            NewFile(File2Id, SubId, "data.csv", 2_000));

        db.SaveChanges();
    }

    private static FileEntry NewFile(int id, int dirId, string name, long size) => new()
    {
        Id = id, VolumeId = Vol1Id, DirectoryId = dirId,
        Name = name, Extension = name[(name.LastIndexOf('.') + 1)..],
        Category = FileCategory.Document, SizeBytes = size,
        IsPresent = true, IsIncluded = true, IsMaterialized = true,
        FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
        LastIndexedUtc = DateTime.UtcNow
    };

    private static CancellationToken None => CancellationToken.None;

    // ── CopyFile ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyFile_within_one_volume_demands_its_full_size()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        dto.State.Should().Be("Pending");
        dto.Type.Should().Be("CopyFile");
        dto.IsIntraVolume.Should().BeTrue();
        // The heart of step 15a: same volume, and still a real demand for room.
        dto.TotalBytes.Should().Be(1_000);
        dto.RequiredBytesTarget.Should().Be(1_000);
        // Nothing is freed: the original stays exactly where it is.
        dto.FreedBytesSource.Should().Be(0);
        dto.SourcePath.Should().Be(@"Docs\report.txt");
        dto.TargetPath.Should().Be(@"Docs\Sub\report.txt");
    }

    [Fact]
    public async Task MoveFile_within_one_volume_still_demands_nothing()
    {
        // The other direction of the same guard: making Copy pay must not make Move pay.
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        dto.IsIntraVolume.Should().BeTrue();
        dto.RequiredBytesTarget.Should().Be(0);
        dto.FreedBytesSource.Should().Be(0);
    }

    [Fact]
    public async Task CopyFile_across_volumes_frees_nothing_on_the_source()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Archivio"
        }, None);

        dto.State.Should().Be("Pending");
        dto.IsIntraVolume.Should().BeFalse();
        dto.RequiredBytesTarget.Should().Be(1_000);
        // A cross-volume MOVE would report 1 000 here; a copy leaves the source untouched.
        dto.FreedBytesSource.Should().Be(0);
    }

    [Fact]
    public async Task CopyFile_onto_its_own_directory_is_refused_at_enqueue()
    {
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = "Docs"
        }, None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*copia su se stesso*");
    }

    [Fact]
    public async Task CopyFile_that_does_not_fit_is_Blocked_never_refused()
    {
        // Vol2 holds 5 000 free; ask for more than that from a single copy.
        using (var db = _harness.CreateContext())
        {
            var f = await db.Files.FirstAsync(x => x.Id == File1Id);
            f.SizeBytes = 9_000;
            await db.SaveChangesAsync();
        }

        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Archivio"
        }, None);

        // §4 — an enqueue is never rejected for space; it is parked and revalued.
        dto.State.Should().Be("Blocked");
        dto.BlockReason.Should().Be("InsufficientSpace");
    }

    [Fact]
    public async Task CopyFile_that_does_not_fit_on_its_OWN_volume_is_Blocked_too()
    {
        // The case the old model could not express at all: the target volume IS the source volume.
        using (var db = _harness.CreateContext())
        {
            var f = await db.Files.FirstAsync(x => x.Id == File1Id);
            f.SizeBytes = 20_000;                  // Vol1 has 10 000 free
            await db.SaveChangesAsync();
        }

        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        dto.State.Should().Be("Blocked");
        dto.BlockReason.Should().Be("InsufficientSpace");
        dto.IsIntraVolume.Should().BeTrue();
    }

    // ── CopyFolder ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyFolder_within_one_volume_expands_the_subtree_and_demands_its_bytes()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFolder,
            SourceDirectoryId = DocsId,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = "Backup"
        }, None);

        dto.State.Should().Be("Pending");
        dto.IsIntraVolume.Should().BeTrue();
        dto.TargetPath.Should().Be(@"Backup\Docs");

        // A folder MOVE within a volume is one marker item and no bytes. A folder COPY has to
        // duplicate every file, so it expands on BOTH sides of the volume question.
        dto.RequiredBytesTarget.Should().Be(3_000);
        dto.FreedBytesSource.Should().Be(0);

        using var db = _harness.CreateContext();
        var items = await db.OperationJobItems.Where(i => i.JobId == dto.Id).ToListAsync();
        items.Should().HaveCount(3);                                   // marker + two files
        items.Count(i => i.FileId == null).Should().Be(1);
    }

    [Fact]
    public async Task MoveFolder_within_one_volume_still_expands_to_a_single_marker()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = SubId,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = ""
        }, None);

        dto.RequiredBytesTarget.Should().Be(0);

        using var db = _harness.CreateContext();
        var items = await db.OperationJobItems.Where(i => i.JobId == dto.Id).ToListAsync();
        items.Should().ContainSingle().Which.FileId.Should().BeNull();
    }

    [Fact]
    public async Task CopyFolder_into_its_own_subtree_is_refused_at_enqueue()
    {
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFolder,
            SourceDirectoryId = DocsId,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*dentro se stessa*");
    }

    [Fact]
    public async Task CopyFolder_onto_its_own_position_is_refused_at_enqueue()
    {
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFolder,
            SourceDirectoryId = SubId,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = "Docs"
        }, None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*gia in questa posizione*");
    }

    // ── preview ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_of_an_intra_volume_copy_is_not_free()
    {
        var f = await Svc().PreviewAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        // The preview reuses the enqueue's engine (§7); if it answered 0 here the picker would
        // promise room the enqueue then refuses.
        f.RequiredBytes.Should().Be(1_000);
    }

    [Fact]
    public async Task Preview_of_an_intra_volume_folder_copy_counts_the_whole_subtree()
    {
        var f = await Svc().PreviewAsync(new CreateJobRequest
        {
            Type = JobType.CopyFolder,
            SourceDirectoryId = DocsId,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = "Backup"
        }, None);

        f.RequiredBytes.Should().Be(3_000);
    }
}
