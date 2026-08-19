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
/// C25 — one user gesture is one call and one transaction. These tests are about what the
/// batch enqueue promises: all the jobs or none of them, the conflict guard asked for every
/// element, sequence numbers still unique, and the batch weighing on the target volume as a
/// whole instead of each file pretending to be alone on the drive.
/// Real SQLite, real ledger, real guard — nothing about the enqueue is stubbed.
/// </summary>
public sealed class QueueBatchEnqueueTests : IDisposable
{
    private const int SourceVolId = 1;   // plenty of room, online
    private const int TargetVolId = 2;   // 2 500 bytes free, online
    private const int DirId = 1;         // "Docs" on the source volume
    private const int FileAId = 1;       // "a.bin" 2 000 bytes
    private const int FileBId = 2;       // "b.bin" 2 000 bytes
    private const int FileCId = 3;       // "c.bin"   500 bytes

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly JobCancellationRegistry _cancellation = new();

    public QueueBatchEnqueueTests()
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
                Id = SourceVolId, VolumeGuid = @"\\?\Volume{src}\",
                FileSystem = "NTFS", FreeBytesLastKnown = 1_000_000, IsOnline = true
            },
            new Volume
            {
                Id = TargetVolId, VolumeGuid = @"\\?\Volume{tgt}\",
                FileSystem = "NTFS", FreeBytesLastKnown = 2_500, IsOnline = true
            });

        db.Directories.Add(new DirectoryNode
        {
            Id = DirId, VolumeId = SourceVolId, Name = "Docs",
            MaterializedPath = "Docs", IsMaterialized = true
        });

        db.Files.AddRange(
            File(FileAId, "a.bin", 2_000),
            File(FileBId, "b.bin", 2_000),
            File(FileCId, "c.bin", 500));

        db.SaveChanges();
    }

    private static FileEntry File(int id, string name, long size) => new()
    {
        Id = id, VolumeId = SourceVolId, DirectoryId = DirId,
        Name = name, Extension = "bin", Category = FileCategory.Other,
        SizeBytes = size, IsPresent = true, IsIncluded = true,
        FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
        LastIndexedUtc = DateTime.UtcNow
    };

    private static CreateJobRequest Move(int fileId, string folder = "") => new()
    {
        Type = JobType.MoveFile,
        SourceFileId = fileId,
        TargetVolumeId = TargetVolId,
        TargetRelativePath = folder
    };

    private static CancellationToken None => CancellationToken.None;

    // ── the happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueues_every_request_of_the_batch_in_request_order()
    {
        var dtos = await Svc().EnqueueBatchAsync([Move(FileCId), Move(FileAId)], None);

        dtos.Should().HaveCount(2);
        dtos[0].SourcePath.Should().Be(@"Docs\c.bin");
        dtos[1].SourcePath.Should().Be(@"Docs\a.bin");

        using var db = _harness.CreateContext();
        db.OperationJobs.Should().HaveCount(2);
        // C26/9c: the numbers still come from inside the transaction, one each.
        db.OperationJobs.Select(j => j.SequenceOrder).ToList()
            .Should().OnlyHaveUniqueItems().And.BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task A_batch_of_one_behaves_exactly_like_a_single_enqueue()
    {
        var dto = await Svc().EnqueueAsync(Move(FileCId), None);

        dto.State.Should().Be("Pending");
        using var db = _harness.CreateContext();
        db.OperationJobs.Should().HaveCount(1);
        db.SpaceLedgerEntries.Count(e => e.IsActive).Should().Be(2); // +target, −source
    }

    // ── atomicity ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_request_that_is_invalid_in_itself_leaves_the_queue_untouched()
    {
        var act = async () => await Svc().EnqueueBatchAsync(
            [Move(FileCId), Move(fileId: 999), Move(FileAId)], None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Elemento 2 di 3*")
            .And.Message.Should().Contain("Nessuna operazione è stata accodata");

        using var db = _harness.CreateContext();
        // Not "one job fewer": NOTHING. A half-enqueued selection is what makes the retry
        // dangerous, and the retry is the user's obvious next move.
        db.OperationJobs.Should().BeEmpty();
        db.OperationJobItems.Should().BeEmpty();
        db.SpaceLedgerEntries.Should().BeEmpty();
        db.Files.Where(f => f.PendingState != EntityPendingState.None).Should().BeEmpty();
    }

    /// <summary>
    /// The behaviour the batch replaces, kept as a test so the difference is a fact and not a
    /// claim: enqueuing the same three requests one call at a time leaves the first one in the
    /// queue when the second is rejected. Nothing here is wrong per call — that is precisely why
    /// the client must not be the thing that decides "a selection".
    /// </summary>
    [Fact]
    public async Task One_call_per_item_is_what_leaves_the_queue_half_filled()
    {
        var svc = Svc();
        await svc.EnqueueAsync(Move(FileCId), None);
        var act = async () => await svc.EnqueueAsync(Move(fileId: 999), None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        using var db = _harness.CreateContext();
        db.OperationJobs.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_empty_batch_is_a_bad_request_not_a_silent_no_op()
    {
        var act = async () => await Svc().EnqueueBatchAsync([], None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── the guard is asked for every element ──────────────────────────────────

    [Fact]
    public async Task The_second_operation_on_the_same_entity_is_parked_behind_the_first()
    {
        var dtos = await Svc().EnqueueBatchAsync([Move(FileCId), Move(FileCId, "Sub")], None);

        dtos[0].State.Should().Be("Pending");
        // §5 — one pending operation per entity, inside a batch exactly as across two calls.
        dtos[1].State.Should().Be("Blocked");
        dtos[1].BlockReason.Should().Be(nameof(JobBlockReason.DependencyPending));
        dtos[1].DependsOnJobId.Should().Be(dtos[0].Id);

        using var db = _harness.CreateContext();
        // Only the job that owns the entity wrote the overlay (§5).
        var file = db.Files.Single(f => f.Id == FileCId);
        file.PendingJobId.Should().Be(dtos[0].Id);
    }

    // ── the batch weighs as one demand ────────────────────────────────────────

    [Fact]
    public async Task Two_moves_that_fit_alone_but_not_together_park_the_second_one()
    {
        // 2 000 + 2 000 bytes onto a volume with 2 500 free: each fits on its own, the pair
        // does not. Judged one by one against untouched free space, both would be born
        // Pending and the deficit would only surface at execution.
        var dtos = await Svc().EnqueueBatchAsync([Move(FileAId), Move(FileBId)], None);

        dtos[0].State.Should().Be("Pending");
        dtos[1].State.Should().Be("Blocked");
        dtos[1].BlockReason.Should().Be(nameof(JobBlockReason.InsufficientSpace));

        using var db = _harness.CreateContext();
        // The parked job holds no reservation — only the one that fits does.
        db.SpaceLedgerEntries.Count(e => e.IsActive && e.VolumeId == TargetVolId).Should().Be(1);
    }

    [Fact]
    public async Task The_reservations_of_a_batch_are_all_visible_to_the_next_caller()
    {
        await Svc().EnqueueBatchAsync([Move(FileCId), Move(FileAId)], None);

        // 2 500 free − 500 − 2 000 = 0 left: a further 500-byte move cannot fit.
        var feasibility = await Svc().PreviewAsync(Move(FileCId), None);

        feasibility.Feasible.Should().BeFalse();
        feasibility.ReservedBytes.Should().Be(2_500);
    }
}
