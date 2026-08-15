using FileTracert.Business.Operations;
using FileTracert.Business.Projection;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Step 9b — the projection model (§5). Queuing an operation must mutate the projection
/// immediately: the entity is shown under its new name, in its new folder, on its new volume,
/// and the search index finds the projected name. Everything runs against the real
/// <see cref="QueueService"/>, the real <see cref="OverlayWriter"/> and a real SQLite database
/// with a real FTS5 table — nothing about the projection is faked.
/// </summary>
public sealed class ProjectionOverlayTests : IDisposable
{
    private const int Vol1Id = 1;   // 10 000 bytes free, online
    private const int Vol2Id = 2;   //  5 000 bytes free, online
    private const int RootDirId = 1;   // ""          on Vol1
    private const int DocsDirId = 2;   // "Docs"      on Vol1
    private const int SubDirId = 3;    // "Docs\Sub"  on Vol1
    private const int Vol2RootId = 4;  // ""          on Vol2
    private const int File1Id = 1;  // "report.txt" 1 000 bytes in Docs
    private const int File2Id = 2;  // "data.csv"   2 000 bytes in Docs\Sub

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly JobCancellationRegistry _cancellation = new();

    public ProjectionOverlayTests()
    {
        _harness = new SqliteInMemoryContext();

        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            // EnsureCreated builds the EF tables but not virtual ones — the FTS5 table is
            // created by a raw-SQL migration in production, so mirror it here.
            setup.Database.ExecuteSqlRaw("""
                CREATE VIRTUAL TABLE IF NOT EXISTS FileSearchIndex USING fts5(
                    name,
                    path,
                    tokenize="unicode61 remove_diacritics 2 separators '\._-'"
                );
                """);
        }

        _ledger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
        Seed();
    }

    public void Dispose() => _harness.Dispose();

    // ── harness ──────────────────────────────────────────────────────────────

    private static IServiceScopeFactory CreateScopeFactory(SqliteInMemoryContext h)
    {
        var services = new ServiceCollection();
        services.AddScoped<FileTracertDbContext>(_ => h.CreateContext());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private QueueService Svc(params IInterceptor[] interceptors)
    {
        var db = _harness.CreateContext(interceptors);
        return new QueueService(db, _ledger, _cancellation,
            NSubstitute.Substitute.For<IFileMover>(),
            new QueueSignal(),
            TestProjection.Index(db, new FileSearchIndex(db)),
            TestProjection.Overlay(db, new FileSearchIndex(db)),
            NullLogger<QueueService>.Instance);
    }

    private static CancellationToken None => CancellationToken.None;

    private void Seed()
    {
        using var db = _harness.CreateContext();

        db.Volumes.AddRange(
            new Volume
            {
                Id = Vol1Id, VolumeGuid = @"\\?\Volume{aaa-1}\", Label = "Alpha",
                FileSystem = "NTFS", FreeBytesLastKnown = 10_000, IsOnline = true
            },
            new Volume
            {
                Id = Vol2Id, VolumeGuid = @"\\?\Volume{bbb-2}\", Label = "Beta",
                FileSystem = "NTFS", FreeBytesLastKnown = 5_000, IsOnline = true
            });

        db.Directories.AddRange(
            new DirectoryNode
            {
                Id = RootDirId, VolumeId = Vol1Id, Name = string.Empty,
                MaterializedPath = string.Empty, IsMaterialized = true
            },
            new DirectoryNode
            {
                Id = DocsDirId, VolumeId = Vol1Id, ParentId = RootDirId, Name = "Docs",
                MaterializedPath = "Docs", IsMaterialized = true
            },
            new DirectoryNode
            {
                Id = SubDirId, VolumeId = Vol1Id, ParentId = DocsDirId, Name = "Sub",
                MaterializedPath = @"Docs\Sub", IsMaterialized = true
            },
            new DirectoryNode
            {
                Id = Vol2RootId, VolumeId = Vol2Id, Name = string.Empty,
                MaterializedPath = string.Empty, IsMaterialized = true
            });

        db.Files.AddRange(
            new FileEntry
            {
                Id = File1Id, VolumeId = Vol1Id, DirectoryId = DocsDirId,
                Name = "report.txt", Extension = "txt", Category = FileCategory.Document,
                SizeBytes = 1_000, IsPresent = true, IsIncluded = true,
                FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                LastIndexedUtc = DateTime.UtcNow
            },
            new FileEntry
            {
                Id = File2Id, VolumeId = Vol1Id, DirectoryId = SubDirId,
                Name = "data.csv", Extension = "csv", Category = FileCategory.Document,
                SizeBytes = 2_000, IsPresent = true, IsIncluded = true,
                FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                LastIndexedUtc = DateTime.UtcNow
            });

        db.SaveChanges();

        new FileSearchIndex(db).SyncVolumeFromDbAsync(Vol1Id, None).GetAwaiter().GetResult();
    }

    private async Task<FileEntry> FileAsync(int id)
    {
        await using var db = _harness.CreateContext();
        return await db.Files.AsNoTracking().SingleAsync(f => f.Id == id, None);
    }

    private async Task<DirectoryNode?> DirAsync(int volumeId, string path)
    {
        await using var db = _harness.CreateContext();
        return await db.Directories.AsNoTracking()
            .FirstOrDefaultAsync(d => d.VolumeId == volumeId && d.MaterializedPath == path, None);
    }

    private async Task<IReadOnlyList<int>> SearchNameAsync(string text)
    {
        await using var db = _harness.CreateContext();
        var result = await new FileSearchIndex(db).SearchAsync(new FileSearchQuery(
            Text: text, Scope: SearchScope.Name, Category: null, Extensions: null,
            SizeBytesMin: null, SizeBytesMax: null, ModifiedFrom: null, ModifiedTo: null,
            VolumeId: null, OnlineOnly: false, Sort: SearchSort.Relevance, Desc: false,
            Skip: 0, Take: 50), None);
        return result.Items;
    }

    // ── write: the five job types stamp the overlay ──────────────────────────

    [Fact]
    public async Task RenameFile_enqueue_writes_the_overlay_on_the_file_row()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = File1Id, NewName = "tramonto.txt"
        }, None);

        var file = await FileAsync(File1Id);
        file.PendingName.Should().Be("tramonto.txt");
        file.PendingState.Should().Be(EntityPendingState.PendingRename);
        file.PendingJobId.Should().Be(dto.Id);
        file.Name.Should().Be("report.txt", "the physical fact only changes at execution");
    }

    [Fact]
    public async Task RenameFolder_enqueue_writes_the_overlay_on_the_directory_row()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = DocsDirId, NewName = "Documenti"
        }, None);

        var dir = await DirAsync(Vol1Id, "Docs");
        dir!.PendingName.Should().Be("Documenti");
        dir.PendingState.Should().Be(EntityPendingState.PendingRename);
        dir.PendingJobId.Should().Be(dto.Id);
        dir.MaterializedPath.Should().Be("Docs", "the path only cascades at execution");
    }

    [Fact]
    public async Task MoveFile_enqueue_points_the_file_at_the_target_directory()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = File1Id,
            TargetVolumeId = Vol1Id, TargetRelativePath = @"Docs\Sub"
        }, None);

        var file = await FileAsync(File1Id);
        file.PendingDirectoryId.Should().Be(SubDirId);
        file.PendingState.Should().Be(EntityPendingState.PendingMove);
        file.PendingJobId.Should().Be(dto.Id);
        file.DirectoryId.Should().Be(DocsDirId, "the physical position only changes at execution");
    }

    [Fact]
    public async Task MoveFile_cross_volume_projects_the_file_onto_the_target_volume()
    {
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = File1Id,
            TargetVolumeId = Vol2Id, TargetRelativePath = "Backup"
        }, None);

        var backup = await DirAsync(Vol2Id, "Backup");
        backup.Should().NotBeNull("the target folder must exist in the projection");
        backup!.VolumeId.Should().Be(Vol2Id);

        var file = await FileAsync(File1Id);
        file.PendingDirectoryId.Should().Be(backup.Id);
        file.VolumeId.Should().Be(Vol1Id, "the row moves volume only at execution");
    }

    [Fact]
    public async Task MoveFolder_enqueue_points_the_directory_at_the_target_parent()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder, SourceDirectoryId = SubDirId,
            TargetVolumeId = Vol1Id, TargetRelativePath = string.Empty
        }, None);

        var dir = await DirAsync(Vol1Id, @"Docs\Sub");
        dir!.PendingParentId.Should().Be(RootDirId);
        dir.PendingState.Should().Be(EntityPendingState.PendingMove);
        dir.PendingJobId.Should().Be(dto.Id);
    }

    [Fact]
    public async Task MoveFolder_writes_exactly_one_overlay_never_one_per_descendant()
    {
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder, SourceDirectoryId = DocsDirId,
            TargetVolumeId = Vol2Id, TargetRelativePath = "Archivio"
        }, None);

        await using var db = _harness.CreateContext();
        // Only the moved folder itself carries a PendingMove; its subtree follows through the
        // projected path, not through per-row overlays.
        (await db.Directories.CountAsync(d => d.PendingState == EntityPendingState.PendingMove, None))
            .Should().Be(1);
        (await db.Files.CountAsync(f => f.PendingState != EntityPendingState.None, None))
            .Should().Be(0);
    }

    [Fact]
    public async Task CreateFolder_enqueue_creates_a_projected_directory_row()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder, TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Album 2026"
        }, None);

        var dir = await DirAsync(Vol1Id, @"Docs\Album 2026");
        dir.Should().NotBeNull();
        dir!.IsMaterialized.Should().BeFalse("the folder does not exist on disk yet");
        dir.IsPresent.Should().BeFalse();
        dir.PendingState.Should().Be(EntityPendingState.PendingCreate);
        dir.PendingJobId.Should().Be(dto.Id);
        dir.ParentId.Should().Be(DocsDirId);
    }

    /// <summary>
    /// §5: «se creo in coda la cartella X e poi ci sposto dentro dei file, il secondo job sa che
    /// X esiste anche se fisicamente non esiste ancora». The queued folder must be a legal move
    /// target BEFORE the CreateFolder job has run.
    /// </summary>
    [Fact]
    public async Task A_file_can_be_moved_into_a_folder_that_is_still_only_queued()
    {
        var createJob = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder, TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Album 2026"
        }, None);

        var album = await DirAsync(Vol1Id, @"Docs\Album 2026");

        var moveJob = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = File1Id,
            TargetVolumeId = Vol1Id, TargetRelativePath = @"Docs\Album 2026"
        }, None);

        moveJob.State.Should().Be("Pending");

        var file = await FileAsync(File1Id);
        file.PendingDirectoryId.Should().Be(album!.Id,
            "the move must resolve against the projection, not against the disk");

        // The folder still belongs to its own creation job — the move must not steal it.
        var albumAfter = await DirAsync(Vol1Id, @"Docs\Album 2026");
        albumAfter!.PendingJobId.Should().Be(createJob.Id);
        albumAfter.PendingState.Should().Be(EntityPendingState.PendingCreate);
    }

    // ── write: search sees the projected name ────────────────────────────────

    [Fact]
    public async Task Search_finds_the_projected_name_and_stops_finding_the_old_one()
    {
        (await SearchNameAsync("report")).Should().Contain(File1Id, "arrange");

        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = File1Id, NewName = "tramonto.txt"
        }, None);

        (await SearchNameAsync("tramonto")).Should().Contain(File1Id);
        (await SearchNameAsync("report")).Should().NotContain(File1Id);
    }

    /// <summary>
    /// A queued FOLDER rename must not touch the FTS index (§5): no file name changes, and
    /// rewriting the path column for every file underneath would be tens of thousands of writes
    /// per enqueue. The projected path is what the search RESULT shows, not what it matches on.
    /// </summary>
    [Fact]
    public async Task A_queued_folder_rename_leaves_the_search_index_alone()
    {
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = DocsDirId, NewName = "Documenti"
        }, None);

        (await SearchNameAsync("report")).Should().Contain(File1Id);
        (await SearchNameAsync("data")).Should().Contain(File2Id);
    }

    /// <summary>
    /// A full rebuild (the startup backfill) must produce the same projected names as the
    /// incremental sync, or a restart would silently undo every queued rename in the index.
    /// </summary>
    [Fact]
    public async Task A_full_rebuild_indexes_the_projected_name_too()
    {
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = File1Id, NewName = "tramonto.txt"
        }, None);

        await using (var db = _harness.CreateContext())
            await new FileSearchIndex(db).RebuildAsync(None);

        (await SearchNameAsync("tramonto")).Should().Contain(File1Id);
        (await SearchNameAsync("report")).Should().NotContain(File1Id);
    }

    // ── write: atomicity with the job ────────────────────────────────────────

    /// <summary>
    /// Throws when a save carries a <see cref="SpaceLedgerEntry"/> — i.e. on the LAST write of a
    /// cross-volume enqueue, after the job, the items and the overlay are already staged in the
    /// transaction. Targets the entity rather than a call count so it does not break every time
    /// the enqueue gains or loses an intermediate save.
    /// </summary>
    private sealed class FailOnLedgerWriteInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var touchesLedger = eventData.Context!.ChangeTracker.Entries<SpaceLedgerEntry>()
                .Any(e => e.State == EntityState.Added);
            if (touchesLedger)
                throw new InvalidOperationException("injected failure on the ledger write");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_failed_enqueue_leaves_neither_job_nor_overlay_behind()
    {
        var act = async () => await Svc(new FailOnLedgerWriteInterceptor()).EnqueueAsync(
            new CreateJobRequest
            {
                Type = JobType.MoveFile, SourceFileId = File1Id,
                TargetVolumeId = Vol2Id, TargetRelativePath = "Backup"
            }, None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var db = _harness.CreateContext();
        (await db.OperationJobs.CountAsync(None)).Should().Be(0);
        (await db.Files.CountAsync(f => f.PendingState != EntityPendingState.None, None)).Should().Be(0);
        (await db.Directories.CountAsync(d => d.PendingState != EntityPendingState.None, None))
            .Should().Be(0, "the projected target directory rolled back with the job");
    }
}
