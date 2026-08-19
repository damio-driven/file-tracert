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

    private QueueService Svc(FileTracert.Contracts.Platform.IVolumeProbe? probe = null)
    {
        var db = _harness.CreateContext();
        return new QueueService(db, _ledger, TestProjection.Space(db, _ledger, probe), _cancellation,
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

    [Fact]
    public async Task RenameFolder_rejects_a_directory_the_last_scan_no_longer_found_on_disk()
    {
        using (var db = _harness.CreateContext())
        {
            var dir = db.Directories.Single(d => d.Id == Dir1Id);
            dir.IsPresent = false;
            db.SaveChanges();
        }

        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder,
            SourceDirectoryId = Dir1Id,
            NewName = "Documents"
        }, None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*non è più presente*");
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
        var f = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 4_500, 0, null, null, true, None);
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

    /// <summary>
    /// K4 — the divergence between the two copies of the cross-volume stanza, made visible.
    ///
    /// <para>MoveFolder skipped the space check when the subtree held no bytes; MoveFile ran it
    /// whatever the size. The VERDICT is the same either way — a demand of zero can never be
    /// infeasible, because the ledger clamps available space at zero — so what actually differed
    /// is what the two did on the way to the same answer: MoveFile probed the DEVICE for free
    /// space, and then stamped the job's <c>EstimateIsLive</c> with whether the drive answered.</para>
    ///
    /// <para>A drive that is flagged online and does not answer is exactly where they part: the
    /// zero-byte MoveFile came out marked "estimate not live", the identical MoveFolder did not,
    /// and the Coda would put a staleness flag on a job that has no number to qualify. MoveFolder's
    /// reading survives — no estimate was made, so none is described — and the syscall (plus its
    /// Warning) disappears with it. "Needs space" is <c>SpaceLedger.ReservationFor</c> now, one
    /// predicate, and it says no here as it does everywhere else.</para>
    /// </summary>
    [Fact]
    public async Task A_zero_byte_cross_volume_move_neither_probes_the_drive_nor_claims_an_estimate()
    {
        int emptyFileId;
        using (var db = _harness.CreateContext())
        {
            var empty = new FileEntry
            {
                VolumeId = Vol1Id, DirectoryId = Dir1Id,
                Name = "empty.txt", Extension = "txt", Category = FileCategory.Document,
                SizeBytes = 0, IsPresent = true, IsIncluded = true,
                FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                LastIndexedUtc = DateTime.UtcNow,
            };
            db.Files.Add(empty);
            db.SaveChanges();
            emptyFileId = empty.Id;
        }

        // A target the catalog believes is connected and that does not answer the probe.
        var probe = new StubFreeSpaceProbe(null);

        var dto = await Svc(probe).EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = emptyFileId,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Backup",
        }, None);

        dto.State.Should().Be("Pending", "a move that asks for no bytes cannot fail to fit");
        dto.BlockReason.Should().Be("None");
        dto.EstimateIsLive.Should().BeTrue(
            "no estimate was made, so nothing here may be described as stale — same as MoveFolder");
        probe.Probes.Should().Be(0,
            "asking the device about a verdict that is decided in advance costs a syscall for nothing");
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

        var f = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 4_500, 0, null, null, true, None);
        f.Feasible.Should().BeFalse();
        f.AvailableEstimateBytes.Should().Be(4_000);
    }

    // ── Guard: one pending op per entity ─────────────────────────────────────
    //
    // What the guard DECIDES (conflict detection in every direction, CreateFolder, targets,
    // casing) and what it does about it (Blocked(DependencyPending), not a rejection) live in
    // JobDependencyEnqueueTests. What stays here is the queue's own side of the contract: a
    // resolved job stops blocking, an unrelated one never did.

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
    public async Task Preview_quotes_the_drive_and_says_the_figure_is_live()
    {
        // The catalog believes 5 000 are free on Vol2; the drive really holds 42 000.
        var f = await Svc(new StubFreeSpaceProbe(42_000)).PreviewAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Backup"
        }, None);

        f.AvailableEstimateBytes.Should().Be(42_000, "the preview asks the device, like the engine does");
        f.EstimateIsLive.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_of_an_unreachable_volume_falls_back_and_admits_it()
    {
        var f = await Svc(new StubFreeSpaceProbe(null)).PreviewAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Backup"
        }, None);

        f.AvailableEstimateBytes.Should().Be(5_000, "the last known figure is still worth showing");
        f.EstimateIsLive.Should().BeFalse("but it must never be dressed up as live");
        f.Feasible.Should().BeTrue("§4: planning never refuses on a volume it cannot see");
    }

    [Fact]
    public async Task A_move_the_real_disk_cannot_hold_is_born_Blocked_not_refused()
    {
        var dto = await Svc(new StubFreeSpaceProbe(10)).EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile,
            SourceFileId = File1Id,        // 1 000 bytes, and the drive holds 10
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Backup"
        }, None);

        dto.State.Should().Be("Blocked");
        dto.BlockReason.Should().Be("InsufficientSpace");
        dto.EstimateIsLive.Should().BeTrue("the verdict was taken on a figure read from the drive");
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
        var before = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 4_500, 0, null, null, true, None);
        before.Feasible.Should().BeFalse(); // 5000 - 1000 = 4000 < 4500

        await Svc().CancelAsync(dto.Id, None);

        // After cancel the reservation is released.
        var after = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 4_500, 0, null, null, true, None);
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
        var f = await _ledger.ComputeFeasibilityAsync(Vol2Id, 5_000, true, 2_500, 0, null, null, true, None);
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

    // ── C22: move-into-self / no-op folder moves rejected at enqueue ──────────

    [Fact]
    public async Task MoveFolder_into_its_own_subtree_is_rejected_at_enqueue()
    {
        // Moving "Docs" under "Docs\Sub" would create Docs\Sub\Docs inside the moved tree:
        // the OS Directory.Move would throw at execution — reject with a 400 up front instead.
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = Dir1Id,          // "Docs"
            TargetVolumeId = Vol1Id,
            TargetRelativePath = @"docs\Sub"     // case-flipped on purpose: predicate must be case-insensitive
        }, None);

        await act.Should().ThrowAsync<ArgumentException>();

        using var db = _harness.CreateContext();
        (await db.OperationJobs.CountAsync(None)).Should().Be(0, "no job may be created for an impossible move");
    }

    [Fact]
    public async Task MoveFolder_into_itself_is_rejected_at_enqueue()
    {
        // Target parent == the folder itself → destination "Docs\Docs" inside the moved tree.
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = Dir1Id,          // "Docs"
            TargetVolumeId = Vol1Id,
            TargetRelativePath = "Docs"
        }, None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MoveFolder_to_its_current_location_is_rejected_at_enqueue()
    {
        // "Docs\Sub" moved to parent "Docs" = exactly where it already is: a no-op.
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = Dir2Id,          // "Docs\Sub"
            TargetVolumeId = Vol1Id,
            TargetRelativePath = "Docs"
        }, None);

        await act.Should().ThrowAsync<ArgumentException>();

        using var db = _harness.CreateContext();
        (await db.OperationJobs.CountAsync(None)).Should().Be(0);
    }

    [Fact]
    public async Task MoveFolder_cross_volume_to_same_path_is_allowed()
    {
        // Same relative path but a DIFFERENT volume is a real move, not a no-op.
        var dto = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder,
            SourceDirectoryId = Dir2Id,          // "Docs\Sub" on Vol1
            TargetVolumeId = Vol2Id,
            TargetRelativePath = "Docs"
        }, None);

        dto.State.Should().Be("Pending");
    }

    // ── FIX #14: cancel mid-flight reconciles items already landed on the target ──

    /// <summary>Cross-volume MoveFile checkpointed at <paramref name="jobState"/> with its
    /// single item at <paramref name="itemState"/> — the shape a cancel finds after a
    /// shutdown between Verifying and DeletingSource.</summary>
    private int SeedCrossMoveJob(JobState jobState, JobItemState itemState)
    {
        using var db = _harness.CreateContext();
        var job = new OperationJob
        {
            Type = JobType.MoveFile,
            State = jobState,
            IsIntraVolume = false,
            SourceVolumeId = Vol1Id,
            TargetVolumeId = Vol2Id,
            TargetRelativePath = @"Backup\report.txt",
            TotalBytes = 1_000,
            RequiredBytesTarget = 1_000,
            SequenceOrder = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        job.Items.Add(new OperationJobItem
        {
            FileId = File1Id,
            SourceRelativePath = @"Docs\report.txt",
            TargetRelativePath = @"Backup\report.txt",
            SizeBytes = 1_000,
            State = itemState,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        db.OperationJobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    [Fact]
    public async Task Cancel_mid_Verifying_indexes_the_finalized_copy_on_the_target()
    {
        // The item is Verified: its copy is already finalized (renamed to the real name)
        // on the target. Cancelling must not orphan that file — the index keeps it.
        int jobId = SeedCrossMoveJob(JobState.Verifying, JobItemState.Verified);

        await Svc().CancelAsync(jobId, None);

        await using var db = _harness.CreateContext();
        var file = await db.Files.Include(f => f.Directory).SingleAsync(f => f.Id == File1Id);
        file.VolumeId.Should().Be(Vol2Id, "the finalized copy physically lives on the target");
        file.Directory.VolumeId.Should().Be(Vol2Id);
        file.Directory.MaterializedPath.Should().Be("Backup");
    }

    [Fact]
    public async Task Cancel_mid_DeletingSource_leaves_no_ghost_at_the_source()
    {
        // The item is Done: the source copy is already in the recycle bin. Cancelling must
        // not leave the Files row pointing at a location that no longer exists (ghost in
        // Catalogo/Ricerca) while the real file sits untracked on the target.
        int jobId = SeedCrossMoveJob(JobState.DeletingSource, JobItemState.Done);

        await Svc().CancelAsync(jobId, None);

        await using var db = _harness.CreateContext();
        var file = await db.Files.Include(f => f.Directory).SingleAsync(f => f.Id == File1Id);
        file.VolumeId.Should().Be(Vol2Id, "the only remaining physical copy is on the target");
        file.Directory.MaterializedPath.Should().Be("Backup");
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
