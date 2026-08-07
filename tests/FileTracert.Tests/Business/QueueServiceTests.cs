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
/// Unit tests for <see cref="QueueService"/>: enqueue, guard, preview, cancel, list.
/// Uses a real SQLite in-memory DB. FK enforcement is disabled because the test
/// verifies business logic, not referential integrity.
/// </summary>
public sealed class QueueServiceTests : IDisposable
{
    // Stable IDs for seeded entities — must match what Seed() inserts.
    private const int Vol1Id = 1;  // 10 000 bytes free, online
    private const int Vol2Id = 2;  //  5 000 bytes free, online
    private const int Dir1Id = 1;  // "Docs"      on Vol1
    private const int Dir2Id = 2;  // "Docs\\Sub" on Vol1
    private const int File1Id = 1; // "report.txt" 1 000 bytes in Docs
    private const int File2Id = 2; // "data.csv"   2 000 bytes in Docs\\Sub

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;

    public QueueServiceTests()
    {
        _harness = new SqliteInMemoryContext();

        // Disable FK enforcement — these tests verify business math, not schema constraints.
        using (var setup = _harness.CreateContext())
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

        _ledger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
        Seed();
    }

    public void Dispose() => _harness.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IServiceScopeFactory CreateScopeFactory(SqliteInMemoryContext h)
    {
        var services = new ServiceCollection();
        services.AddScoped<FileTracertDbContext>(_ => h.CreateContext());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private readonly JobCancellationRegistry _cancellation = new();

    private QueueService Svc() =>
        new(_harness.CreateContext(), _ledger, _cancellation,
            NSubstitute.Substitute.For<FileTracert.Contracts.Platform.IFileMover>(),
            new QueueSignal(), NullLogger<QueueService>.Instance);

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
                Id = Dir1Id, VolumeId = Vol1Id, Name = "Docs",
                MaterializedPath = "Docs", IsMaterialized = true
            },
            new DirectoryNode
            {
                Id = Dir2Id, VolumeId = Vol1Id, ParentId = Dir1Id, Name = "Sub",
                MaterializedPath = @"Docs\Sub", IsMaterialized = true
            });

        db.Files.AddRange(
            new FileEntry
            {
                Id = File1Id, VolumeId = Vol1Id, DirectoryId = Dir1Id,
                Name = "report.txt", Extension = "txt", Category = FileCategory.Document,
                SizeBytes = 1_000, IsPresent = true, IsIncluded = true,
                FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                LastIndexedUtc = DateTime.UtcNow
            },
            new FileEntry
            {
                Id = File2Id, VolumeId = Vol1Id, DirectoryId = Dir2Id,
                Name = "data.csv", Extension = "csv", Category = FileCategory.Document,
                SizeBytes = 2_000, IsPresent = true, IsIncluded = true,
                FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                LastIndexedUtc = DateTime.UtcNow
            });

        db.SaveChanges();
    }

    private static CancellationToken None => CancellationToken.None;

    // ── CreateFolder ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFolder_creates_pending_job()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\NewFolder"
        }, None);

        dto.State.Should().Be("Pending");
        dto.Type.Should().Be("CreateFolder");
        dto.IsIntraVolume.Should().BeTrue();
        dto.TotalBytes.Should().Be(0);
        dto.TargetPath.Should().Be(@"Docs\NewFolder");
    }

    // ── RenameFile ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameFile_creates_pending_intra_job()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = "report_v2.txt"
        }, None);

        dto.State.Should().Be("Pending");
        dto.IsIntraVolume.Should().BeTrue();
        dto.SourceVolumeId.Should().Be(Vol1Id);
        dto.TargetVolumeId.Should().Be(Vol1Id);
        dto.SourcePath.Should().Be(@"Docs\report.txt");
        dto.TotalBytes.Should().Be(0);
        dto.TargetPath.Should().Be("report_v2.txt");
    }

    // ── RenameFolder ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameFolder_creates_pending_intra_job()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder,
            SourceDirectoryId = Dir1Id,
            NewName = "Documents"
        }, None);

        dto.State.Should().Be("Pending");
        dto.IsIntraVolume.Should().BeTrue();
        dto.SourcePath.Should().Be("Docs");   // source dir MaterializedPath
        dto.TargetPath.Should().Be("Documents");
    }

    // ── MoveFile intra-volume ─────────────────────────────────────────────────

    [Fact]
    public async Task MoveFile_same_volume_creates_pending_intra_job()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\Sub"
        }, None);

        dto.State.Should().Be("Pending");
        dto.IsIntraVolume.Should().BeTrue();
        dto.TotalBytes.Should().Be(0);
        dto.SourcePath.Should().Be(@"Docs\report.txt");
        dto.TargetPath.Should().Be(@"Docs\Sub\report.txt");
    }

    // ── MoveFile cross-volume feasible ────────────────────────────────────────

    [Fact]
    public async Task MoveFile_cross_volume_feasible_creates_pending_and_reserves()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,    // 1 000 bytes
            TargetVolumeId = Vol2Id,   // 5 000 free → feasible
            TargetRelativePath = "Backup"
        }, None);

        dto.State.Should().Be("Pending");
        dto.IsIntraVolume.Should().BeFalse();
        dto.TotalBytes.Should().Be(1_000);
        dto.RequiredBytesTarget.Should().Be(1_000);
        dto.FreedBytesSource.Should().Be(1_000);
        dto.EstimateIsLive.Should().BeTrue();

        // Ledger must reflect the reservation: 5000 free − 1000 reserved = 4000 available.
        // Asking for 4500 should now be infeasible.
        var f = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 4_500, null, null, true, None);
        f.Feasible.Should().BeFalse();
        f.AvailableEstimateBytes.Should().Be(4_000);
    }

    // ── MoveFile cross-volume infeasible → Blocked ────────────────────────────

    [Fact]
    public async Task MoveFile_cross_volume_infeasible_creates_blocked_job()
    {
        // Pre-fill the ledger so vol2 effectively has only 400 bytes available.
        await _ledger.ReserveAsync(999, 0, Vol2Id, 4_600, null, 0, None);

        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,  // 1 000 bytes > 400 available
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Backup"
        }, None);

        dto.State.Should().Be("Blocked");
        dto.Id.Should().BeGreaterThan(0);
    }

    // ── C3: enqueue + reserve are atomic (no overcommit window) ───────────────

    [Fact]
    public async Task Enqueue_persists_the_reservation_atomically_with_the_job()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,     // 1 000 bytes, Vol1 → Vol2
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Backup"
        }, None);

        dto.State.Should().Be("Pending");

        // The reservation is DURABLE — committed in the same transaction as the job, not left in the
        // in-memory mirror only. Wiping the mirror and rebuilding it purely from persisted rows must
        // still reflect the reservation; under the old post-commit reserve, a failure there would
        // leave the job Pending with no DB entry and this rebuild would show nothing.
        await _ledger.RebuildFromDbAsync(None);

        using (var db = _harness.CreateContext())
        {
            (await db.OperationJobs.CountAsync(None)).Should().Be(1);
            (await db.SpaceLedgerEntries.CountAsync(e => e.JobId == dto.Id && e.IsActive, None))
                .Should().Be(2); // +reservation on target, −liberation on source
        }

        var f = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 4_500, null, null, true, None);
        f.Feasible.Should().BeFalse();
        f.AvailableEstimateBytes.Should().Be(4_000);
    }

    // ── Guard: one pending op per entity ─────────────────────────────────────

    [Fact]
    public async Task Guard_throws_EntityAlreadyPending_on_second_file_op()
    {
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = "v2.txt"
        }, None);

        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = "v3.txt"
        }, None);

        await act.Should().ThrowAsync<EntityAlreadyPendingException>()
            .Where(e => e.EntityType == "File" && e.EntityId == File1Id);
    }

    [Fact]
    public async Task Guard_throws_EntityAlreadyPending_on_second_directory_op()
    {
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder,
            SourceDirectoryId = Dir1Id,
            NewName = "OldDocs"
        }, None);

        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = Dir1Id,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = "Archive"
        }, None);

        await act.Should().ThrowAsync<EntityAlreadyPendingException>()
            .Where(e => e.EntityType == "Directory" && e.EntityId == Dir1Id);
    }

    [Fact]
    public async Task Guard_allows_re_enqueue_after_cancel()
    {
        var svc = Svc();
        var dto = await svc.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = "v2.txt"
        }, None);

        await Svc().CancelAsync(dto.Id, None);

        // Should succeed now that the first job is cancelled.
        var dto2 = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = "v3.txt"
        }, None);

        dto2.State.Should().Be("Pending");
    }

    // ── C2: overlap guard (ancestor / descendant), not just exact match ───────

    [Fact]
    public async Task Guard_blocks_op_on_a_descendant_of_a_pending_directory()
    {
        // Pending op on "Docs" (Dir1) must block an op on its child "Docs\Sub" (Dir2).
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = Dir1Id, NewName = "Documents"
        }, None);

        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder, SourceDirectoryId = Dir2Id,
            TargetVolumeId = Vol1Id, TargetRelativePath = "Archive"
        }, None);

        await act.Should().ThrowAsync<EntityAlreadyPendingException>()
            .Where(e => e.EntityType == "Directory" && e.EntityId == Dir2Id);
    }

    [Fact]
    public async Task Guard_blocks_op_on_an_ancestor_of_a_pending_directory()
    {
        // Reverse direction: a pending op on the child "Docs\Sub" must block an op on "Docs".
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = Dir2Id, NewName = "SubRenamed"
        }, None);

        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = Dir1Id, NewName = "Documents"
        }, None);

        await act.Should().ThrowAsync<EntityAlreadyPendingException>()
            .Where(e => e.EntityType == "Directory" && e.EntityId == Dir1Id);
    }

    [Fact]
    public async Task Guard_allows_op_on_a_non_overlapping_sibling_directory()
    {
        const int Dir3Id = 3;
        using (var db = _harness.CreateContext())
        {
            db.Directories.Add(new DirectoryNode
            {
                Id = Dir3Id, VolumeId = Vol1Id, Name = "Media",
                MaterializedPath = "Media", IsMaterialized = true
            });
            db.SaveChanges();
        }

        // Pending op on "Docs" must NOT block an op on the unrelated sibling "Media".
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = Dir1Id, NewName = "Documents"
        }, None);

        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = Dir3Id, NewName = "Pictures"
        }, None);

        dto.State.Should().Be("Pending");
    }

    // ── Preview ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_intra_volume_always_feasible_no_db_write()
    {
        var f = await Svc().PreviewAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = "v2.txt"
        }, None);

        f.Feasible.Should().BeTrue();
        f.RequiredBytes.Should().Be(0);

        // No job should have been created.
        using var db = _harness.CreateContext();
        (await db.OperationJobs.CountAsync(None)).Should().Be(0);
    }

    [Fact]
    public async Task Preview_cross_volume_returns_feasibility_without_db_write()
    {
        var f = await Svc().PreviewAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,    // 1 000 bytes
            TargetVolumeId = Vol2Id,   // 5 000 free
            TargetRelativePath = "Backup"
        }, None);

        f.Feasible.Should().BeTrue();
        f.RequiredBytes.Should().Be(1_000);
        f.AvailableEstimateBytes.Should().Be(5_000);

        using var db = _harness.CreateContext();
        (await db.OperationJobs.CountAsync(None)).Should().Be(0);
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_transitions_job_to_Cancelled()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = "v2.txt"
        }, None);

        await Svc().CancelAsync(dto.Id, None);

        using var db = _harness.CreateContext();
        var job = await db.OperationJobs.FindAsync([dto.Id]);
        job!.State.Should().Be(JobState.Cancelled);
        job.CompletedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Cancel_releases_ledger_reservation()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,    // 1 000 bytes on vol2
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Backup"
        }, None);

        // Ledger has a reservation before cancel.
        var before = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 4_500, null, null, true, None);
        before.Feasible.Should().BeFalse(); // 5000 - 1000 = 4000 < 4500

        await Svc().CancelAsync(dto.Id, None);

        // After cancel the reservation is released.
        var after = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 4_500, null, null, true, None);
        after.Feasible.Should().BeTrue(); // 5000 ≥ 4500
    }

    // ── FIX #5: batch preview evaluates the TOTAL demand, not just the first file ──

    [Fact]
    public async Task PreviewBatch_reports_the_deficit_of_the_whole_batch()
    {
        // Vol2 effectively has 400 bytes available (5 000 free − 4 600 reserved).
        await _ledger.ReserveAsync(999, 0, Vol2Id, 4_600, null, 0, None);

        // File1 (1 000) + File2 (2 000) = 3 000 total. Previewing only the first file
        // would report deficit 600 — the real batch deficit is 2 600.
        var f = await Svc().PreviewBatchAsync(
        [
            new CreateJobRequest { Type = JobType.MoveFile, SourceFileId = File1Id, TargetVolumeId = Vol2Id, TargetRelativePath = "Backup" },
            new CreateJobRequest { Type = JobType.MoveFile, SourceFileId = File2Id, TargetVolumeId = Vol2Id, TargetRelativePath = "Backup" },
        ], None);

        f.Feasible.Should().BeFalse();
        f.RequiredBytes.Should().Be(3_000);
        f.DeficitBytes.Should().Be(2_600);
        f.BlockingVolumeId.Should().Be(Vol2Id);
    }

    [Fact]
    public async Task PreviewBatch_is_feasible_when_the_whole_batch_fits()
    {
        var f = await Svc().PreviewBatchAsync(
        [
            new CreateJobRequest { Type = JobType.MoveFile, SourceFileId = File1Id, TargetVolumeId = Vol2Id, TargetRelativePath = "Backup" },
            new CreateJobRequest { Type = JobType.MoveFile, SourceFileId = File2Id, TargetVolumeId = Vol2Id, TargetRelativePath = "Backup" },
        ], None);

        f.Feasible.Should().BeTrue();
        f.RequiredBytes.Should().Be(3_000); // aggregated, not just the first file
    }

    [Fact]
    public async Task PreviewBatch_with_only_intra_volume_moves_is_trivially_feasible()
    {
        var f = await Svc().PreviewBatchAsync(
        [
            new CreateJobRequest { Type = JobType.MoveFile, SourceFileId = File1Id, TargetVolumeId = Vol1Id, TargetRelativePath = "Elsewhere" },
        ], None);

        f.Feasible.Should().BeTrue();
        f.RequiredBytes.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_throws_when_job_is_already_terminal()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = "v2.txt"
        }, None);

        var svc = Svc();
        await svc.CancelAsync(dto.Id, None);

        var act = async () => await Svc().CancelAsync(dto.Id, None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal*");
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_returns_jobs_ordered_by_SequenceOrder()
    {
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = "v2.txt"
        }, None);

        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = "NewDir"
        }, None);

        var page = await Svc().ListAsync(0, 10, None);

        page.TotalCount.Should().Be(2);
        page.Items.Should().HaveCount(2);
        page.Items[0].SequenceOrder.Should().BeLessThan(page.Items[1].SequenceOrder);
    }

    [Fact]
    public async Task List_paging_skip_take_works()
    {
        for (int i = 0; i < 5; i++)
        {
            await Svc().EnqueueAsync(new CreateJobRequest
            {
                Type = JobType.CreateFolder,
                TargetVolumeId = Vol1Id,
                TargetRelativePath = $"Dir{i}"
            }, None);
        }

        var page = await Svc().ListAsync(skip: 2, take: 2, None);

        page.TotalCount.Should().Be(5);
        page.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_attaches_feasibility_for_blocked_jobs()
    {
        // Fill vol2 so the next move is infeasible.
        await _ledger.ReserveAsync(998, 0, Vol2Id, 4_600, null, 0, None);

        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Backup"
        }, None);

        dto.State.Should().Be("Blocked");

        var page = await Svc().ListAsync(0, 10, None);
        var item = page.Items.First(j => j.Id == dto.Id);

        item.Feasibility.Should().NotBeNull();
        item.Feasibility!.Feasible.Should().BeFalse();
        item.Feasibility.DeficitBytes.Should().BeGreaterThan(0);
    }

    // ── MoveFolder ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveFolder_same_volume_creates_single_item_intra_job()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = Dir1Id,  // "Docs"
            TargetVolumeId = Vol1Id,
            TargetRelativePath = "Archive"
        }, None);

        dto.State.Should().Be("Pending");
        dto.IsIntraVolume.Should().BeTrue();
        dto.TotalBytes.Should().Be(0);
        dto.TargetPath.Should().Be(@"Archive\Docs");

        using var db = _harness.CreateContext();
        var items = await db.OperationJobItems.Where(i => i.JobId == dto.Id).ToListAsync(None);
        items.Should().HaveCount(1);
        items[0].SourceRelativePath.Should().Be("Docs");
        items[0].TargetRelativePath.Should().Be(@"Archive\Docs");
    }

    [Fact]
    public async Task MoveFolder_cross_volume_expands_to_per_file_items()
    {
        // Dir1 = "Docs" contains file1 (report.txt)
        // Dir2 = "Docs\\Sub" (child of Dir1) contains file2 (data.csv)
        // Subtree = {Dir1, Dir2} → 2 files: 1000 + 2000 = 3000 bytes

        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = Dir1Id,
            TargetVolumeId = Vol2Id,     // cross-volume
            TargetRelativePath = "Archive"
        }, None);

        dto.State.Should().Be("Pending");
        dto.IsIntraVolume.Should().BeFalse();
        dto.TotalBytes.Should().Be(3_000);
        dto.TargetPath.Should().Be(@"Archive\Docs");

        using var db = _harness.CreateContext();
        var items = await db.OperationJobItems
            .Where(i => i.JobId == dto.Id)
            .OrderBy(i => i.SourceRelativePath)
            .ToListAsync(None);

        // 1 folder marker (the moved folder itself, C21) + 2 file items.
        items.Should().HaveCount(3);

        var markerItem = items.Single(i => i.FileId == null);
        markerItem.SourceRelativePath.Should().Be("Docs");
        markerItem.TargetRelativePath.Should().Be(@"Archive\Docs");
        markerItem.SizeBytes.Should().Be(0);

        var csvItem = items.First(i => i.SourceRelativePath.EndsWith("data.csv"));
        csvItem.TargetRelativePath.Should().Be(@"Archive\Docs\Sub\data.csv");

        var txtItem = items.First(i => i.SourceRelativePath.EndsWith("report.txt"));
        txtItem.TargetRelativePath.Should().Be(@"Archive\Docs\report.txt");

        // Ledger must hold the 3 000-byte reservation on vol2.
        var f = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 2_500, null, null, true, None);
        f.AvailableEstimateBytes.Should().Be(2_000);
    }

    [Fact]
    public async Task Preview_MoveFolder_cross_volume_uses_subtree_size_not_zero()
    {
        // Docs subtree = report.txt (1000) + data.csv (2000) = 3000 bytes.
        // Vol2 has 5000 free → feasible, and requiredBytes must be the subtree weight.
        var f = await Svc().PreviewAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = Dir1Id,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Archive"
        }, None);

        f.RequiredBytes.Should().Be(3_000);
        f.Feasible.Should().BeTrue();

        // Preview must not write anything.
        using var db = _harness.CreateContext();
        (await db.OperationJobs.CountAsync(None)).Should().Be(0);
    }

    // ── name / path validation (folder ops) ────────────────────────────────────

    [Theory]
    [InlineData(@"a\b")]   // separator
    [InlineData("a/b")]    // forward separator
    [InlineData("..")]     // traversal
    [InlineData(".")]      // current
    [InlineData("C:")]     // drive
    [InlineData("")]       // empty
    [InlineData("   ")]    // whitespace
    public async Task RenameFolder_rejects_invalid_name(string badName)
    {
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder,
            SourceDirectoryId = Dir1Id,
            NewName = badName
        }, None);

        await act.Should().ThrowAsync<ArgumentException>();

        using var db = _harness.CreateContext();
        (await db.OperationJobs.CountAsync(None)).Should().Be(0);
    }

    [Theory]
    [InlineData(@"a\b")]
    [InlineData("..")]
    [InlineData("")]
    public async Task RenameFile_rejects_invalid_name(string badName)
    {
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile,
            SourceFileId = File1Id,
            NewName = badName
        }, None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(@"Foo\..\Bar")]  // traversal in the middle
    [InlineData(@"C:\Foo")]      // rooted / drive
    [InlineData("")]             // empty → must name a folder
    public async Task CreateFolder_rejects_invalid_path(string badPath)
    {
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = badPath
        }, None);

        await act.Should().ThrowAsync<ArgumentException>();

        using var db = _harness.CreateContext();
        (await db.OperationJobs.CountAsync(None)).Should().Be(0);
    }

    [Fact]
    public async Task CreateFolder_accepts_nested_valid_path()
    {
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder,
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"Docs\New Album"
        }, None);

        dto.State.Should().Be("Pending");
        dto.TargetPath.Should().Be(@"Docs\New Album");
    }
}
