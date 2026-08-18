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
/// End-to-end integration tests that drive the REAL queue-processor loop (peek next runnable by
/// SequenceOrder → execute → revalue Blocked jobs on completion — exactly what
/// <c>QueueProcessorWorker</c> does) against the REAL <see cref="SpaceLedger"/>, REAL
/// <see cref="JobExecutionEngine"/> and REAL <see cref="Win32FileMover"/> operating on real files
/// in a throwaway temp directory. Nothing here mocks the component under test.
///
/// Cross-volume is simulated with two Volume rows that share the same physical volume GUID
/// (so the mover reads/writes real temp files) but distinct Ids (so the ledger tracks them as
/// independent volumes). "Free space" is the per-volume <c>FreeBytesLastKnown</c> estimate the
/// tests seed; the fold-at-completion + revaluation is what these tests exercise.
///
/// SAFETY: every path lives under <see cref="Path.GetTempPath"/> + a fresh GUID, created and
/// deleted by the test. No user paths, no production WatchedRoots — safe for unattended CI.
/// </summary>
[Trait("Category", "Platform")]
public sealed class QueueWorkerLoopE2ETests : IDisposable
{
    private const int Vol1Id = 1;              // roomy source volume
    private const int Vol2Id = 2;              // tight target volume
    private const long Vol2Free = 10;          // only 10 bytes free at start

    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly Win32FileMover _mover;
    private readonly JobCancellationRegistry _cancellation = new();
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public QueueWorkerLoopE2ETests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
        {
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            // Two ledger volumes must share ONE real physical GUID so the real mover reads/writes
            // real temp files; the production unique index on VolumeGuid would block that. Dropping
            // it is a test-harness accommodation (like FK-off), not a change to business logic.
            setup.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_Volumes_VolumeGuid");
        }

        var services = new ServiceCollection();
        var harness = _harness;
        services.AddScoped<FileTracertDbContext>(_ => harness.CreateContext());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _ledger = new SpaceLedger(scopeFactory, NullLogger<SpaceLedger>.Instance);

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
        _absRoot = Path.Combine(tempPath, $"ft-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_absRoot);
        _relRoot = Path.GetRelativePath(_mountPoint, _absRoot);
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_absRoot))
            Directory.Delete(_absRoot, recursive: true);
    }

    private string R(params string[] parts) => Path.Combine([_relRoot, .. parts]);
    private string Abs(string rel) => Path.GetFullPath(Path.Combine(_mountPoint, rel));

    // ── wiring ────────────────────────────────────────────────────────────────

    private void SeedVolumes()
    {
        using var db = _harness.CreateContext();
        // Both rows point at the SAME physical GUID (real mover) but are distinct ledger volumes.
        db.Volumes.AddRange(
            new Volume { Id = Vol1Id, VolumeGuid = _volumeGuid, FileSystem = "NTFS", FreeBytesLastKnown = 1_000_000, IsOnline = true },
            new Volume { Id = Vol2Id, VolumeGuid = _volumeGuid, FileSystem = "NTFS", FreeBytesLastKnown = Vol2Free, IsOnline = true });
        db.SaveChanges();
    }

    private QueueService MakeQueue(FileTracertDbContext db) =>
        new(db, _ledger, _cancellation, _mover, new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db),
            TestProjection.Unblocker(db),
            TestProjection.Revaluator(db, _ledger),
            TestProjection.Realtime(), NullLogger<QueueService>.Instance);

    private JobExecutionEngine MakeEngine(FileTracertDbContext db)
    {
        var indexUpdater = TestProjection.Index(db);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db);
        return new JobExecutionEngine(db, _mover, _ledger, indexUpdater, TestProjection.Overlay(db), notifications,
            TimeProvider.System, TestProjection.Realtime(), NullLogger<JobExecutionEngine>.Instance);
    }

    private BlockedJobRevaluator MakeRevaluator(FileTracertDbContext db) =>
        new(db, _ledger, TestProjection.Unblocker(db), TestProjection.Realtime(), NullLogger<BlockedJobRevaluator>.Instance);

    /// <summary>Directly seed a cross-volume MoveFile job over one real source file.</summary>
    private async Task<int> EnqueueMoveAsync(int fileId, int srcVol, int tgtVol, string tgtDirRel)
    {
        await using var db = _harness.CreateContext();
        var dto = await MakeQueue(db).EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = fileId, TargetVolumeId = tgtVol, TargetRelativePath = tgtDirRel,
        }, CancellationToken.None);
        return dto.Id;
    }

    private int SeedFile(int id, int volId, string dirRel, string name, string content)
    {
        Directory.CreateDirectory(Abs(dirRel));
        File.WriteAllText(Abs(Path.Combine(dirRel, name)), content);

        using var db = _harness.CreateContext();
        int dirId = 100 + id;
        db.Directories.Add(new DirectoryNode
        {
            Id = dirId, VolumeId = volId, Name = Path.GetFileName(dirRel),
            MaterializedPath = dirRel, IsMaterialized = true,
        });
        db.Files.Add(new FileEntry
        {
            Id = id, VolumeId = volId, DirectoryId = dirId, Name = name, Extension = "bin",
            Category = FileCategory.Other, SizeBytes = content.Length, IsPresent = true, IsIncluded = true,
            FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    /// <summary>Faithful copy of QueueProcessorWorker's runnable pick: FIFO by SequenceOrder.</summary>
    private async Task<int?> PeekNextRunnableAsync()
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs
            .Where(j => JobStates.Runnable.Contains(j.State))
            .OrderBy(j => j.SequenceOrder)
            .Select(j => (int?)j.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>The real worker loop: pick → execute → revalue on completion. Returns completion order.</summary>
    private async Task<List<int>> RunWorkerLoopAsync(int maxIterations = 12)
    {
        var completedOrder = new List<int>();
        var seenBlockedTwice = new Dictionary<int, int>();

        for (int i = 0; i < maxIterations; i++)
        {
            int? jobId = await PeekNextRunnableAsync();
            if (jobId is null) break;

            await using (var db = _harness.CreateContext())
                await MakeEngine(db).ExecuteJobAsync(jobId.Value, CancellationToken.None);

            var state = await ReadState(jobId.Value);
            if (state == JobState.Completed)
            {
                completedOrder.Add(jobId.Value);
                await using var db = _harness.CreateContext();
                await MakeRevaluator(db).RevaluateAsync(CancellationToken.None);
            }
            else if (state is JobState.Failed or JobState.Blocked)
            {
                // A job that stays non-runnable would spin the loop forever if it were still
                // picked — Runnable excludes Blocked/Failed, so the loop advances. Guard anyway.
                seenBlockedTwice[jobId.Value] = seenBlockedTwice.GetValueOrDefault(jobId.Value) + 1;
                if (seenBlockedTwice[jobId.Value] > 2) break;
            }
        }

        return completedOrder;
    }

    private async Task<JobState> ReadState(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.OperationJobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FIFO_auto_recovery_end_to_end_over_real_files()
    {
        SeedVolumes();
        // A frees Vol2 (moves a 50-byte file OFF it); B needs Vol2 (moves a 55-byte file ONTO it).
        SeedFile(1, Vol2Id, R("v2src"), "off.bin", new string('a', 50));
        SeedFile(2, Vol1Id, R("v1src"), "onto.bin", new string('b', 55));

        int jobA = await EnqueueMoveAsync(1, Vol2Id, Vol1Id, R("v1dst"));
        int jobB = await EnqueueMoveAsync(2, Vol1Id, Vol2Id, R("v2dst"));

        // B is only feasible thanks to A's promised liberation (planning view at enqueue).
        (await ReadState(jobB)).Should().Be(JobState.Pending);

        var order = await RunWorkerLoopAsync();

        order.Should().Equal(jobA, jobB);
        (await ReadState(jobA)).Should().Be(JobState.Completed);
        (await ReadState(jobB)).Should().Be(JobState.Completed);

        // Real files landed at their destinations; sources went to the recycle bin.
        File.Exists(Abs(R("v1dst", "off.bin"))).Should().BeTrue();
        File.Exists(Abs(R("v2dst", "onto.bin"))).Should().BeTrue();
        File.ReadAllText(Abs(R("v2dst", "onto.bin"))).Should().Be(new string('b', 55));

        // No orphan partials anywhere under the scratch root.
        Directory.EnumerateFiles(_absRoot, "*.fadit-partial", SearchOption.AllDirectories)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Anticipated_dependent_job_blocks_then_recovers_in_the_loop()
    {
        SeedVolumes();
        SeedFile(1, Vol2Id, R("v2src"), "off.bin", new string('a', 50));
        SeedFile(2, Vol1Id, R("v1src"), "onto.bin", new string('b', 55));

        int jobA = await EnqueueMoveAsync(1, Vol2Id, Vol1Id, R("v1dst"));
        int jobB = await EnqueueMoveAsync(2, Vol1Id, Vol2Id, R("v2dst"));

        // Anticipate B out of order: the hard re-check refuses the unmaterialized promise.
        await using (var db = _harness.CreateContext())
            await MakeEngine(db).ExecuteJobAsync(jobB, CancellationToken.None);

        (await ReadState(jobB)).Should().Be(JobState.Blocked);
        File.Exists(Abs(R("v2dst", "onto.bin"))).Should().BeFalse("nothing must be copied on a promise");
        Directory.EnumerateFiles(_absRoot, "*.fadit-partial", SearchOption.AllDirectories).Should().BeEmpty();

        // Now the real loop runs A, folds the liberation, revalues B → B completes on its own.
        var order = await RunWorkerLoopAsync();

        order.Should().Equal(jobA, jobB);
        (await ReadState(jobB)).Should().Be(JobState.Completed);
        File.Exists(Abs(R("v2dst", "onto.bin"))).Should().BeTrue();
    }

    [Fact]
    public async Task Failed_job_does_not_stop_the_loop_and_leaves_no_partial()
    {
        SeedVolumes();
        // Job A will fail: its source file is indexed but physically absent → real copy throws.
        using (var db = _harness.CreateContext())
        {
            db.Directories.Add(new DirectoryNode { Id = 200, VolumeId = Vol1Id, Name = "ghost", MaterializedPath = R("ghost"), IsMaterialized = true });
            db.Files.Add(new FileEntry
            {
                Id = 1, VolumeId = Vol1Id, DirectoryId = 200, Name = "missing.bin", Extension = "bin",
                Category = FileCategory.Other, SizeBytes = 20, IsPresent = true, IsIncluded = true,
                FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        // Job B is healthy and must still complete after A fails.
        SeedFile(2, Vol1Id, R("v1src"), "good.bin", new string('b', 30));

        int jobA = await EnqueueMoveAsync(1, Vol1Id, Vol2Id, R("v2dst"));
        int jobB = await EnqueueMoveAsync(2, Vol1Id, Vol1Id, R("v1dst")); // intra-volume, no space contention

        // Give Vol2 room so A's failure is the copy, not a space block.
        using (var db = _harness.CreateContext())
        {
            db.Volumes.Single(v => v.Id == Vol2Id).FreeBytesLastKnown = 1_000_000;
            db.SaveChanges();
        }

        var order = await RunWorkerLoopAsync();

        (await ReadState(jobA)).Should().Be(JobState.Failed);
        order.Should().Contain(jobB, "a failed job must not stall the queue");
        (await ReadState(jobB)).Should().Be(JobState.Completed);
        Directory.EnumerateFiles(_absRoot, "*.fadit-partial", SearchOption.AllDirectories)
            .Should().BeEmpty("a Failed job cleans its own partials");
    }
}
