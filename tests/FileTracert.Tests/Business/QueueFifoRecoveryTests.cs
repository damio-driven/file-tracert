using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// FIX #2-FIFO — end-to-end queue tests against the REAL <see cref="QueueService"/> (enqueue with
/// real ledger reservations), REAL <see cref="SpaceLedger"/> and REAL <see cref="JobExecutionEngine"/>
/// (only the mover is substituted — disk I/O is not under test here).
///
/// Scenario: job A frees space on V2 (move OFF V2), job B needs that space on V2 (move ONTO V2).
/// B is feasible at enqueue only thanks to A's promised liberation (FIFO planning view, §4).
/// The execution-time HARD re-check must NOT trust that promise: attempted before A has freed,
/// B must go Blocked(InsufficientSpace) — never proceed on estimate, never end Failed — and must
/// recover on its own once A completes (revaluation cycle).
/// </summary>
public sealed class QueueFifoRecoveryTests : IDisposable
{
    private const int Vol1Id = 1;              // roomy volume
    private const int Vol2Id = 2;              // tight volume: 10 000 bytes free
    private const string Vol1Guid = @"\\?\Volume{aaa-1}\";
    private const string Vol2Guid = @"\\?\Volume{bbb-2}\";
    private const long Vol2Free = 10_000;
    private const long SizeA = 50_000;         // job A moves this OFF V2 (frees 50 000)
    private const long SizeB = 55_000;         // job B moves this ONTO V2 (fits only after A)

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly JobCancellationRegistry _cancellation = new();

    public QueueFifoRecoveryTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            Seed(setup);
        }

        var services = new ServiceCollection();
        var harness = _harness;
        services.AddScoped<FileTracertDbContext>(_ => harness.CreateContext());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _ledger = new SpaceLedger(scopeFactory, NullLogger<SpaceLedger>.Instance);
    }

    public void Dispose() => _harness.Dispose();

    // ── seeding ───────────────────────────────────────────────────────────────

    private static void Seed(FileTracertDbContext db)
    {
        db.Volumes.AddRange(
            new Volume { Id = Vol1Id, VolumeGuid = Vol1Guid, FileSystem = "NTFS", FreeBytesLastKnown = 200_000, IsOnline = true },
            new Volume { Id = Vol2Id, VolumeGuid = Vol2Guid, FileSystem = "NTFS", FreeBytesLastKnown = Vol2Free, IsOnline = true });

        db.Directories.AddRange(
            new DirectoryNode { Id = 1, VolumeId = Vol1Id, Name = "Data", MaterializedPath = "Data", IsMaterialized = true },
            new DirectoryNode { Id = 2, VolumeId = Vol2Id, Name = "Big", MaterializedPath = "Big", IsMaterialized = true });

        db.Files.AddRange(
            new FileEntry
            {
                Id = 1, VolumeId = Vol2Id, DirectoryId = 2, Name = "off-v2.bin", Extension = "bin",
                Category = FileCategory.Other, SizeBytes = SizeA, IsPresent = true, IsIncluded = true,
                FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
            },
            new FileEntry
            {
                Id = 2, VolumeId = Vol1Id, DirectoryId = 1, Name = "onto-v2.bin", Extension = "bin",
                Category = FileCategory.Other, SizeBytes = SizeB, IsPresent = true, IsIncluded = true,
                FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
            });

        db.SaveChanges();
    }

    // ── wiring (real services, fresh DbContext each) ──────────────────────────

    private QueueService MakeQueueService(FileTracertDbContext db) =>
        new(db, _ledger, _cancellation, Substitute.For<IFileMover>(), NullLogger<QueueService>.Instance);

    private JobExecutionEngine MakeEngine(IFileMover mover, FileTracertDbContext db)
    {
        var indexUpdater = new IndexUpdater(db, new FakeFileSearchIndex(), NullLogger<IndexUpdater>.Instance);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db);
        return new JobExecutionEngine(db, mover, _ledger, indexUpdater, notifications,
            TimeProvider.System, NullLogger<JobExecutionEngine>.Instance);
    }

    private BlockedJobRevaluator MakeRevaluator(FileTracertDbContext db) =>
        new(db, _ledger, NullLogger<BlockedJobRevaluator>.Instance);

    private static IFileMover HappyMover()
    {
        var mover = Substitute.For<IFileMover>();
        mover.CopyFileAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Func<long, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        mover.Verify(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
             .Returns(true);
        return mover;
    }

    private async Task<(int jobAId, int jobBId)> EnqueueScenarioAsync()
    {
        await using var db = _harness.CreateContext();
        var queue = MakeQueueService(db);

        var jobA = await queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = 1, TargetVolumeId = Vol1Id, TargetRelativePath = "Data",
        }, CancellationToken.None);

        var jobB = await queue.EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = 2, TargetVolumeId = Vol2Id, TargetRelativePath = "Big",
        }, CancellationToken.None);

        return (jobA.Id, jobB.Id);
    }

    /// <summary>Same query the worker runs: lowest SequenceOrder among runnable states.</summary>
    private async Task<int?> PeekNextRunnableAsync()
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs
            .Where(j => JobStates.Runnable.Contains(j.State))
            .OrderBy(j => j.SequenceOrder)
            .Select(j => (int?)j.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Simulates the QueueProcessorWorker main loop: pick next runnable (FIFO), execute,
    /// revalue Blocked jobs after every completion. Returns execution order of completed jobs.
    /// </summary>
    private async Task<List<int>> RunWorkerLoopAsync(int maxIterations = 10)
    {
        var completedOrder = new List<int>();

        for (int i = 0; i < maxIterations; i++)
        {
            int? jobId = await PeekNextRunnableAsync();
            if (jobId is null) break;

            await using (var db = _harness.CreateContext())
            {
                await MakeEngine(HappyMover(), db).ExecuteJobAsync(jobId.Value, CancellationToken.None);
            }

            if (await ReadState(jobId.Value) == JobState.Completed)
            {
                completedOrder.Add(jobId.Value);
                await using var db = _harness.CreateContext();
                await MakeRevaluator(db).RevaluateAsync(CancellationToken.None);
            }
        }

        return completedOrder;
    }

    private async Task<JobState> ReadState(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync();
    }

    private async Task<JobBlockReason> ReadBlockReason(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.Where(j => j.Id == jobId).Select(j => j.BlockReason).SingleAsync();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task B_enqueued_after_A_is_feasible_at_enqueue_thanks_to_A_promised_liberation()
    {
        var (_, jobBId) = await EnqueueScenarioAsync();

        // Planning view (§4): A's −50 000 on V2 precedes B in the queue, so B is Pending, not Blocked.
        (await ReadState(jobBId)).Should().Be(JobState.Pending);
    }

    [Fact]
    public async Task B_attempted_before_A_goes_Blocked_not_Failed_and_never_copies_on_a_promise()
    {
        var (_, jobBId) = await EnqueueScenarioAsync();

        // Anticipate B (out of FIFO order): A has not freed anything yet, so the 55 000 bytes
        // are NOT physically on V2. The hard re-check must refuse the promise.
        var mover = HappyMover();
        await using (var db = _harness.CreateContext())
        {
            await MakeEngine(mover, db).ExecuteJobAsync(jobBId, CancellationToken.None);
        }

        (await ReadState(jobBId)).Should().Be(JobState.Blocked,
            "the execution-time re-check must not copy on the strength of an unmaterialized liberation");
        (await ReadBlockReason(jobBId)).Should().Be(JobBlockReason.InsufficientSpace);

#pragma warning disable CS4014
        mover.DidNotReceive().CopyFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Func<long, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
#pragma warning restore CS4014

        // …and once A completes, the revaluation cycle must bring B back on its own (auto-recovery).
        var order = await RunWorkerLoopAsync();

        (await ReadState(jobBId)).Should().Be(JobState.Completed,
            "after A frees its space the revaluation must re-run B without user intervention");
        order.Should().EndWith(jobBId);
    }

    [Fact]
    public async Task Worker_loop_executes_A_before_B_and_both_complete()
    {
        var (jobAId, jobBId) = await EnqueueScenarioAsync();

        var order = await RunWorkerLoopAsync();

        order.Should().Equal(jobAId, jobBId);
        (await ReadState(jobAId)).Should().Be(JobState.Completed);
        (await ReadState(jobBId)).Should().Be(JobState.Completed);
    }
}
