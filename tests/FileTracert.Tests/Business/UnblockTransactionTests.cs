using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// E8 — releasing a parked job is ONE write transaction.
///
/// It used to be three: the state change and its overlay committed on the service's own
/// connection, then <c>ISpaceLedger.ReleaseAsync</c> opened a scope and a connection of its own,
/// then <c>ReserveAsync</c> opened another. On SQLite, which has exactly one writer, three
/// transactions per released job is three turns at the only lock in the process — and a
/// revaluation pass releases jobs in a loop.
///
/// The correctness that had to survive is the one WP1 established for the terminal transitions
/// (finding #5): the durable ledger move belongs in the SAME transaction as the state change. It
/// did not survive before — it followed the commit — so this is not just cheaper, it closes the
/// window in which a crash left a Pending job with its reservation released and not re-taken,
/// i.e. under-reserved against every other job in the queue.
/// </summary>
public sealed class UnblockTransactionTests : IDisposable
{
    private const int SrcVolId = 1;
    private const int TgtVolId = 2;
    private const int DirId = 1;
    private const int FileId = 1;
    private const long FileSize = 1_000;

    private readonly SqliteInMemoryContext _harness = new();
    private readonly SpaceLedger _ledger;
    private readonly CountingCommandInterceptor _sql = new();

    public UnblockTransactionTests()
    {
        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            Seed(setup);
        }

        // The counter goes on the LEDGER's scope factory as well, not only on the revaluator's
        // context: the whole point of the finding is the two transactions the ledger used to open
        // on scopes of its own, and a counter that could not see them would be measuring nothing.
        var services = new ServiceCollection();
        var harness = _harness;
        var sql = _sql;
        services.AddScoped<FileTracertDbContext>(_ => harness.CreateContext(sql));
        _ledger = new SpaceLedger(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SpaceLedger>.Instance);
    }

    public void Dispose() => _harness.Dispose();

    // ── correctness: the ledger ends up exactly as it did before ──────────────

    [Fact]
    public async Task A_released_job_ends_with_exactly_one_reservation_and_one_liberation()
    {
        var jobId = SeedBlockedJob(hadReservation: false);

        (await Revaluate()).Should().Be(1);

        (await ReadJob(jobId)).State.Should().Be(JobState.Pending);

        var active = await ActiveLedgerRows(jobId);
        active.Should().HaveCount(2);
        active.Should().ContainSingle(e => e.VolumeId == TgtVolId && e.DeltaBytes == +FileSize);
        active.Should().ContainSingle(e => e.VolumeId == SrcVolId && e.DeltaBytes == -FileSize);
    }

    /// <summary>
    /// The other half of "release-then-reserve normalises": a job parked by the engine kept its
    /// reservation, so the release must end with one set, not two. Doing the deactivate and the
    /// insert in one transaction must not change that.
    /// </summary>
    [Fact]
    public async Task A_job_that_already_held_a_reservation_does_not_end_up_holding_two()
    {
        var jobId = SeedBlockedJob(hadReservation: true);

        (await Revaluate()).Should().Be(1);

        var active = await ActiveLedgerRows(jobId);
        active.Should().HaveCount(2);
        active.Sum(e => e.DeltaBytes).Should().Be(0, "one +reservation and one -liberation");

        // The superseded rows are deactivated, never deleted (§6, no hard delete).
        await using var db = _harness.CreateContext();
        var all = await db.SpaceLedgerEntries.AsNoTracking().Where(e => e.JobId == jobId).ToListAsync();
        all.Should().HaveCount(4);
        all.Count(e => !e.IsActive).Should().Be(2);
    }

    /// <summary>
    /// The in-memory mirror is what the feasibility of the NEXT job is judged against, so it has
    /// to agree with the rows after the commit — the whole point of doing it after, and of doing
    /// both halves rather than only the reserve.
    /// </summary>
    [Fact]
    public async Task The_in_memory_mirror_agrees_with_the_committed_rows()
    {
        var jobId = SeedBlockedJob(hadReservation: true);

        await Revaluate();

        // A second job wanting the whole drive must now see this job's reservation standing in
        // its way — which it can only do from the mirror.
        var feasibility = await _ledger.ComputeFeasibilityAsync(
            TgtVolId, freeBytes: FileSize, estimateIsLive: true, requiredBytes: FileSize,
            marginBytes: 0, excludeJobId: null, sequenceOrder: 99,
            includeQueuedLiberations: false, CancellationToken.None);

        feasibility.ReservedBytes.Should().Be(FileSize);
        feasibility.Feasible.Should().BeFalse();
        jobId.Should().BePositive();
    }

    /// <summary>An intra-volume job reserves nothing, and must still be released.</summary>
    [Fact]
    public async Task An_intra_volume_job_is_released_without_touching_the_ledger()
    {
        var jobId = SeedBlockedJob(hadReservation: false, intraVolume: true);

        (await Revaluate()).Should().Be(1);

        (await ReadJob(jobId)).State.Should().Be(JobState.Pending);
        (await ActiveLedgerRows(jobId)).Should().BeEmpty();
    }

    // ── cost: one transaction ─────────────────────────────────────────────────

    /// <summary>
    /// The measurement, and the reason this finding exists: on SQLite a transaction is a turn at
    /// the only write lock in the process, and a revaluation pass releases jobs in a loop.
    ///
    /// The number counted is EXPLICIT transactions — 2 before, 1 now. The finding says three
    /// writes, and it is right: the third is the ledger's release, a lone
    /// <c>ExecuteUpdate</c> for which EF opens no transaction of its own. It was still a separate
    /// write on a separate scope, and it is now inside this one.
    /// </summary>
    [Fact]
    public async Task Releasing_a_job_opens_one_transaction_not_three()
    {
        SeedBlockedJob(hadReservation: true);

        await using var db = _harness.CreateContext(_sql);
        var revaluator = TestProjection.Revaluator(db, _ledger);
        _sql.Reset();

        (await revaluator.RevaluateAsync(CancellationToken.None)).Should().Be(1);

        _sql.Transactions.Should().Be(1,
            "the state change, the overlay and both halves of the ledger are one unit of work");
    }

    /// <summary>
    /// The ledger's durable rows and the state change land TOGETHER: the transaction is the unit,
    /// so a failure after the state has been set leaves neither. Forced with a trigger that
    /// refuses any INSERT into the ledger — the last write of the unit of work, and the one that
    /// used to happen on its own connection AFTER the state had already been committed.
    /// </summary>
    [Fact]
    public async Task A_failure_while_writing_the_ledger_leaves_the_job_blocked()
    {
        var jobId = SeedBlockedJob(hadReservation: true);

        await using (var db = _harness.CreateContext())
        {
            // Make any INSERT into the ledger fail, without touching the rows already there.
            db.Database.ExecuteSqlRaw(
                "CREATE TRIGGER refuse_ledger BEFORE INSERT ON SpaceLedgerEntries " +
                "BEGIN SELECT RAISE(ABORT, 'no'); END");
        }

        await using (var db = _harness.CreateContext())
        {
            var revaluator = TestProjection.Revaluator(db, _ledger);
            var act = async () => await revaluator.RevaluateAsync(CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();
        }

        await using (var db = _harness.CreateContext())
            db.Database.ExecuteSqlRaw("DROP TRIGGER refuse_ledger");

        // Nothing committed: the job is still parked, and its old reservation is still standing.
        var job = await ReadJob(jobId);
        job.State.Should().Be(JobState.Blocked);
        (await ActiveLedgerRows(jobId)).Should().HaveCount(2, "the deactivate rolled back too");
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private async Task<int> Revaluate()
    {
        await using var db = _harness.CreateContext();
        return await TestProjection.Revaluator(db, _ledger).RevaluateAsync(CancellationToken.None);
    }

    private int SeedBlockedJob(bool hadReservation, bool intraVolume = false)
    {
        using var db = _harness.CreateContext();

        var job = new OperationJob
        {
            Type = JobType.MoveFile,
            State = JobState.Blocked,
            BlockReason = JobBlockReason.InsufficientSpace,
            IsIntraVolume = intraVolume,
            SourceVolumeId = SrcVolId,
            TargetVolumeId = intraVolume ? SrcVolId : TgtVolId,
            TargetRelativePath = intraVolume ? @"Docs\Archivio\report.txt" : @"Archivio\report.txt",
            TotalBytes = FileSize,
            RequiredBytesTarget = intraVolume ? 0 : FileSize,
            FreedBytesSource = intraVolume ? 0 : FileSize,
            SequenceOrder = 1,
        };
        job.Items.Add(new OperationJobItem
        {
            FileId = FileId,
            SourceRelativePath = @"Docs\report.txt",
            TargetRelativePath = job.TargetRelativePath!,
            SizeBytes = FileSize,
            State = JobItemState.Pending,
        });
        db.OperationJobs.Add(job);
        db.SaveChanges();

        if (hadReservation)
        {
            db.SpaceLedgerEntries.AddRange(SpaceLedger.BuildReservationEntries(
                job.Id, TgtVolId, FileSize, SrcVolId, FileSize));
            db.SaveChanges();
            _ledger.RegisterReservationInMemoryAsync(
                job.Id, job.SequenceOrder, TgtVolId, FileSize, SrcVolId, FileSize,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        return job.Id;
    }

    private async Task<OperationJob> ReadJob(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private async Task<List<SpaceLedgerEntry>> ActiveLedgerRows(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.SpaceLedgerEntries.AsNoTracking()
            .Where(e => e.JobId == jobId && e.IsActive).ToListAsync();
    }

    private static void Seed(FileTracertDbContext db)
    {
        db.Volumes.AddRange(
            new Volume
            {
                Id = SrcVolId, VolumeGuid = @"\\?\Volume{aaa-1}\", Label = "Origine",
                FileSystem = "NTFS", FreeBytesLastKnown = 100_000, IsOnline = true,
            },
            new Volume
            {
                Id = TgtVolId, VolumeGuid = @"\\?\Volume{bbb-2}\", Label = "Destinazione",
                FileSystem = "NTFS", FreeBytesLastKnown = 100_000, IsOnline = true,
            });

        db.Directories.Add(new DirectoryNode
        {
            Id = DirId, VolumeId = SrcVolId, Name = "Docs",
            MaterializedPath = "Docs", IsMaterialized = true,
        });

        db.Files.Add(new FileEntry
        {
            Id = FileId, VolumeId = SrcVolId, DirectoryId = DirId,
            Name = "report.txt", Extension = "txt", Category = FileCategory.Document,
            SizeBytes = FileSize, IsPresent = true, IsIncluded = true,
            FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
            LastIndexedUtc = DateTime.UtcNow,
        });

        db.SaveChanges();
    }
}
