using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// C26 — the FIFO position of a job must be unique and assigned inside the transaction that
/// inserts it. The ledger's feasibility walk skips entries with a higher <c>SequenceOrder</c>,
/// so two jobs sharing one number stop seeing each other's reservations and both conclude they
/// fit on a volume that only has room for one.
///
/// The race is reproduced deterministically with an interceptor that, at the exact moment the
/// enqueue is about to INSERT its job, slips a competing job in on the same number — which is
/// what a second API request that committed between our <c>MAX</c> and our <c>INSERT</c> does.
/// </summary>
public sealed class QueueSequenceOrderTests : IDisposable
{
    private const int Vol1Id = 1;
    private const int Dir1Id = 1;
    private const int File1Id = 1;
    private const int File2Id = 2;

    private readonly SqliteInMemoryContext _harness = new();
    private readonly SpaceLedger _ledger;
    private readonly JobCancellationRegistry _cancellation = new();

    public QueueSequenceOrderTests()
    {
        using (var setup = _harness.CreateContext())
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

        var services = new ServiceCollection();
        services.AddScoped<FileTracertDbContext>(_ => _harness.CreateContext());
        _ledger = new SpaceLedger(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SpaceLedger>.Instance);

        Seed();
    }

    public void Dispose() => _harness.Dispose();

    private QueueService Svc(params IInterceptor[] interceptors)
    {
        var db = _harness.CreateContext(interceptors);
        return new QueueService(db, _ledger, _cancellation,
            NSubstitute.Substitute.For<IFileMover>(), new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db), TestProjection.Guard(db),
            NullLogger<QueueService>.Instance);
    }

    private void Seed()
    {
        using var db = _harness.CreateContext();
        db.Volumes.Add(new Volume
        {
            Id = Vol1Id, VolumeGuid = @"\\?\Volume{aaa-1}\", FileSystem = "NTFS",
            FreeBytesLastKnown = 10_000, IsOnline = true
        });
        db.Directories.Add(new DirectoryNode
        {
            Id = Dir1Id, VolumeId = Vol1Id, Name = "Docs", MaterializedPath = "Docs",
            IsMaterialized = true
        });
        db.Files.AddRange(
            NewFile(File1Id, "report.txt"),
            NewFile(File2Id, "notes.txt"));
        db.SaveChanges();
    }

    private static FileEntry NewFile(int id, string name) => new()
    {
        Id = id, VolumeId = Vol1Id, DirectoryId = Dir1Id, Name = name, Extension = "txt",
        Category = FileCategory.Document, SizeBytes = 100, IsPresent = true, IsIncluded = true,
        FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
        LastIndexedUtc = DateTime.UtcNow
    };

    private static CancellationToken None => CancellationToken.None;

    [Fact]
    public async Task Concurrent_enqueue_cannot_steal_the_same_queue_position()
    {
        // A first job so the contested number is not the trivial 1.
        await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = File1Id, NewName = "v2.txt"
        }, None);

        var intruder = new StealSequenceOrderInterceptor();
        await Svc(intruder).EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = File2Id, NewName = "n2.txt"
        }, None);

        intruder.Stole.Should().BeTrue("the test only proves anything if the race was injected");

        using var db = _harness.CreateContext();
        var orders = await db.OperationJobs.Select(j => j.SequenceOrder).ToListAsync(None);
        orders.Should().OnlyHaveUniqueItems(
            "the queue position decides which reservations a job's feasibility can see");
    }

    [Fact]
    public async Task Migration_renumbers_duplicate_positions_before_the_unique_index()
    {
        // An upgraded database can already hold the duplicates C26 produced. Reproduce that
        // state (drop the index, insert colliding rows) and run the migration's own SQL over it.
        using var db = _harness.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_OperationJobs_SequenceOrder;", None);
        foreach (var (id, order) in new[] { (10, 5), (11, 5), (12, 3), (13, 7), (14, 3) })
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO OperationJobs
                    (Id, Type, State, BlockReason, IsIntraVolume, TotalBytes, BytesProcessed,
                     RequiredBytesTarget, FreedBytesSource, EstimateIsLive, SequenceOrder,
                     RetryCount, CreatedUtc, UpdatedUtc)
                VALUES ({0}, 'RenameFile', 'Pending', 'None', 1, 0, 0, 0, 0, 1, {1}, 0,
                        '2026-01-01', '2026-01-01');
                """, [id, order], None);
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE _seq_renumber AS
                SELECT Id, ROW_NUMBER() OVER (ORDER BY SequenceOrder, Id) AS NewOrder
                FROM OperationJobs;
            """, None);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE OperationJobs
               SET SequenceOrder = (SELECT NewOrder FROM _seq_renumber
                                    WHERE _seq_renumber.Id = OperationJobs.Id);
            """, None);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE _seq_renumber;", None);

        // Uniqueness — otherwise CREATE UNIQUE INDEX would fail and the upgrade would abort.
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IX_OperationJobs_SequenceOrder ON OperationJobs (SequenceOrder);", None);

        var renumbered = await db.OperationJobs.AsNoTracking()
            .OrderBy(j => j.SequenceOrder).Select(j => new { j.Id, j.SequenceOrder })
            .ToListAsync(None);

        renumbered.Select(r => r.SequenceOrder).Should().Equal(1, 2, 3, 4, 5);
        // Relative FIFO order preserved, ties broken by Id: (3,12) (3,14) (5,10) (5,11) (7,13).
        renumbered.Select(r => r.Id).Should().Equal(12, 14, 10, 11, 13);
    }

    /// <summary>
    /// Fires once, on the save that inserts an <see cref="OperationJob"/>, and commits a rival job
    /// on the very <c>SequenceOrder</c> that save is about to use. Raw SQL on the same connection:
    /// going through EF would re-enter the interceptor and the auditing pipeline.
    /// </summary>
    private sealed class StealSequenceOrderInterceptor : SaveChangesInterceptor
    {
        public bool Stole { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Stole) return base.SavingChangesAsync(eventData, result, cancellationToken);

            var inserted = eventData.Context!.ChangeTracker.Entries<OperationJob>()
                .FirstOrDefault(e => e.State == EntityState.Added);
            if (inserted is null) return base.SavingChangesAsync(eventData, result, cancellationToken);

            Stole = true;
            var connection = (SqliteConnection)eventData.Context.Database.GetDbConnection();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction?)eventData.Context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = """
                INSERT INTO OperationJobs
                    (Type, State, BlockReason, IsIntraVolume, TotalBytes, BytesProcessed,
                     RequiredBytesTarget, FreedBytesSource, EstimateIsLive, SequenceOrder,
                     RetryCount, CreatedUtc, UpdatedUtc)
                VALUES ('RenameFile', 'Pending', 'None', 1, 0, 0, 0, 0, 1, $order, 0, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$order", inserted.Entity.SequenceOrder);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
