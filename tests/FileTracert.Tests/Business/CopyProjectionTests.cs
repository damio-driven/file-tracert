using FileTracert.Business.Operations;
using FileTracert.Business.Projection;
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
/// Step 15a — the projection of a queued Copy (§5).
///
/// The second of the three things a Copy makes different: <b>it creates a new entity</b>. Move and
/// Rename mutate the row that is already there and express the promise with its <c>Pending*</c>
/// fields; a copy has no row at the destination to carry them, so one is created ahead of the
/// file. These tests pin down the three halves of that: the row appears with the right flags, it
/// is visible and searchable at once, and when the job dies the row is REMOVED rather than blanked
/// — the single place §6's no-hard-delete does not apply, because that row never stood for a file.
///
/// Real OverlayWriter, real DirectoryResolver, real FTS index over real SQLite.
/// </summary>
public sealed class CopyProjectionTests : IDisposable
{
    private const int Vol1Id = 1;
    private const int Vol2Id = 2;
    private const int RootId = 1;
    private const int DocsId = 2;
    private const int SubId = 3;
    private const int Vol2RootId = 4;
    private const int File1Id = 1;   // Docs\report.txt      1 000 bytes
    private const int File2Id = 2;   // Docs\Sub\data.csv    2 000 bytes

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly JobCancellationRegistry _cancellation = new();

    public CopyProjectionTests()
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
            new Volume { Id = Vol1Id, VolumeGuid = @"\\?\Volume{aaa-1}\", FileSystem = "NTFS", FreeBytesLastKnown = 1_000_000, IsOnline = true },
            new Volume { Id = Vol2Id, VolumeGuid = @"\\?\Volume{bbb-2}\", FileSystem = "NTFS", FreeBytesLastKnown = 1_000_000, IsOnline = true });

        db.Directories.AddRange(
            new DirectoryNode { Id = RootId, VolumeId = Vol1Id, Name = "", MaterializedPath = "", IsMaterialized = true, IsPresent = true },
            new DirectoryNode { Id = DocsId, VolumeId = Vol1Id, ParentId = RootId, Name = "Docs", MaterializedPath = "Docs", IsMaterialized = true, IsPresent = true },
            new DirectoryNode { Id = SubId, VolumeId = Vol1Id, ParentId = DocsId, Name = "Sub", MaterializedPath = @"Docs\Sub", IsMaterialized = true, IsPresent = true },
            new DirectoryNode { Id = Vol2RootId, VolumeId = Vol2Id, Name = "", MaterializedPath = "", IsMaterialized = true, IsPresent = true });

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

    // ── the destination row appears ───────────────────────────────────────────

    [Fact]
    public async Task A_queued_file_copy_puts_a_row_at_the_destination_at_once()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        using var db = _harness.CreateContext();
        var projected = await db.Files.SingleAsync(f => f.DirectoryId == SubId && f.Name == "report.txt");

        projected.Id.Should().NotBe(File1Id, "a copy is a new entity, not the source row moved");
        projected.IsMaterialized.Should().BeFalse("nothing has created it yet");
        projected.IsPresent.Should().BeFalse("and no scan has ever looked for it");
        projected.PendingState.Should().Be(EntityPendingState.PendingCreate);
        projected.PendingJobId.Should().Be(dto.Id);
        projected.SizeBytes.Should().Be(1_000);
        projected.Category.Should().Be(FileCategory.Document);
        // A file that does not exist has no FRN, and the unique (VolumeId, UsnFileRef) index is
        // filtered, so any number of projections coexist.
        projected.UsnFileRef.Should().BeNull();
        // Nor a hash: the content WILL be identical, but claiming one for bytes nobody has written
        // is a lie a verifier could act on.
        projected.QuickHash.Should().BeNull();
        projected.Hash.Should().BeNull();
    }

    [Fact]
    public async Task The_source_row_of_a_copy_is_left_completely_alone()
    {
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        using var db = _harness.CreateContext();
        var source = await db.Files.SingleAsync(f => f.Id == File1Id);

        // A move stamps PendingMove here. A copy changes nothing about the original, so promising
        // the user a change to it would be a lie on the screen.
        source.PendingState.Should().Be(EntityPendingState.None);
        source.PendingJobId.Should().BeNull();
        source.PendingDirectoryId.Should().BeNull();
        source.DirectoryId.Should().Be(DocsId);
        source.IsMaterialized.Should().BeTrue();
        source.IsPresent.Should().BeTrue();
    }

    [Fact]
    public async Task A_copy_to_a_folder_that_does_not_exist_yet_projects_the_folder_too()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Backup\2026"
        }, None);

        using var db = _harness.CreateContext();
        var dir = await db.Directories.SingleAsync(d => d.VolumeId == Vol1Id && d.MaterializedPath == @"Backup\2026");
        dir.IsMaterialized.Should().BeFalse();
        dir.PendingState.Should().Be(EntityPendingState.PendingCreate);
        dir.PendingJobId.Should().Be(dto.Id);

        // …and the intermediate one, through the same walk CreateFolder uses.
        var parent = await db.Directories.SingleAsync(d => d.VolumeId == Vol1Id && d.MaterializedPath == "Backup");
        parent.PendingState.Should().Be(EntityPendingState.PendingCreate);

        (await db.Files.AnyAsync(f => f.DirectoryId == dir.Id && f.Name == "report.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task A_queued_folder_copy_projects_one_row_per_file_it_will_write()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFolder,
            SourceDirectoryId = DocsId,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Archivio"
        }, None);

        using var db = _harness.CreateContext();

        // MoveFolder writes ONE overlay, on the folder row, and the descendants' projected paths
        // fall out of the parent walk. A copy cannot: none of those descendants exist yet.
        var projected = await db.Files
            .Where(f => f.PendingJobId == dto.Id)
            .OrderBy(f => f.Name)
            .ToListAsync();

        projected.Should().HaveCount(2);
        projected.Should().OnlyContain(f => !f.IsMaterialized && !f.IsPresent && f.VolumeId == Vol2Id);
        projected.Select(f => f.Name).Should().BeEquivalentTo(["data.csv", "report.txt"]);

        // The destination tree, including the sub-folder the deeper file needs.
        (await db.Directories.AnyAsync(d => d.VolumeId == Vol2Id && d.MaterializedPath == @"Archivio\Docs"))
            .Should().BeTrue();
        (await db.Directories.AnyAsync(d => d.VolumeId == Vol2Id && d.MaterializedPath == @"Archivio\Docs\Sub"))
            .Should().BeTrue();
    }

    // ── it is visible and searchable at once ──────────────────────────────────

    [Fact]
    public async Task The_projected_row_is_findable_in_the_search_index()
    {
        var db = _harness.CreateContext();
        SqliteFts.Create(db);
        var fts = new FileTracert.Data.Search.FileSearchIndex(db);

        var svc = new QueueService(db, _ledger, TestProjection.Space(db, _ledger), _cancellation,
            NSubstitute.Substitute.For<FileTracert.Contracts.Platform.IFileMover>(),
            new QueueSignal(),
            TestProjection.Index(db, fts),
            new OverlayWriter(db, new DirectoryResolver(db), fts, NullLogger<OverlayWriter>.Instance),
            TestProjection.Unblocker(db, fts),
            TestProjection.Revaluator(db, _ledger, fts),
            TestProjection.Realtime(), NullLogger<QueueService>.Instance);

        var dto = await svc.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        using var check = _harness.CreateContext();
        var projectedId = await check.Files
            .Where(f => f.PendingJobId == dto.Id)
            .Select(f => f.Id)
            .SingleAsync();

        var rows = await check.Database
            .SqlQuery<int>($"SELECT rowid AS Value FROM FileSearchIndex WHERE rowid = {projectedId}")
            .ToListAsync();

        // §5 — the projected name is what gets indexed. Queue fifty copies and the search has to
        // find them before the bytes land, not after.
        rows.Should().ContainSingle();
        db.Dispose();
    }

    // ── and when the job dies, the row goes with it ───────────────────────────

    [Fact]
    public async Task Cancelling_a_copy_REMOVES_the_projected_row_instead_of_blanking_it()
    {
        var svc = Svc();
        var dto = await svc.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        await Svc().CancelAsync(dto.Id, None);

        using var db = _harness.CreateContext();

        // The one place §6's no-hard-delete does not apply: this row never described anything on
        // any disk. Blanking its Pending* fields — what every other operation's cleanup does —
        // would leave an unowned, never-created row behind for ever.
        (await db.Files.AnyAsync(f => f.DirectoryId == SubId && f.Name == "report.txt"))
            .Should().BeFalse();

        // The source is untouched, which is the whole promise of a cancelled copy.
        var source = await db.Files.SingleAsync(f => f.Id == File1Id);
        source.IsPresent.Should().BeTrue();
        source.IsMaterialized.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_a_folder_copy_removes_every_row_it_projected()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFolder,
            SourceDirectoryId = DocsId,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Archivio"
        }, None);

        await Svc().CancelAsync(dto.Id, None);

        using var db = _harness.CreateContext();
        (await db.Files.CountAsync(f => f.VolumeId == Vol2Id)).Should().Be(0);
        (await db.Files.CountAsync()).Should().Be(2, "the two source rows are untouched");
    }

    [Fact]
    public async Task The_startup_reconciliation_removes_an_orphaned_copy_destination()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        // The job vanishes without going through any terminal path — a crash outside the
        // transactions, a hand-edited database, an older build.
        using (var wreck = _harness.CreateContext())
        {
            await wreck.OperationJobItems.Where(i => i.JobId == dto.Id).ExecuteDeleteAsync();
            await wreck.OperationJobs.Where(j => j.Id == dto.Id).ExecuteDeleteAsync();
        }

        using (var db = _harness.CreateContext())
        {
            await TestProjection.Overlay(db).ReconcileOrphansAsync(None);
        }

        using var check = _harness.CreateContext();
        (await check.Files.AnyAsync(f => f.DirectoryId == SubId && f.Name == "report.txt"))
            .Should().BeFalse();
    }

    // ── a re-applied overlay does not double the row ──────────────────────────

    [Fact]
    public async Task Re_applying_the_overlay_reuses_the_row_it_already_created()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CopyFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        using (var db = _harness.CreateContext())
        {
            var job = await db.OperationJobs.Include(j => j.Items).SingleAsync(j => j.Id == dto.Id);
            // What RetryAsync does: re-run ApplyAsync on a job that already owns its overlay.
            await TestProjection.Overlay(db).ApplyAsync(job, [.. job.Items], None);
        }

        using var check = _harness.CreateContext();
        (await check.Files.CountAsync(f => f.DirectoryId == SubId && f.Name == "report.txt"))
            .Should().Be(1);
    }
}
