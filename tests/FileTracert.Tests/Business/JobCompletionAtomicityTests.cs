using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data.Entities;
using FileTracert.Platform;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// Review finding #7: the index update used to run AFTER the Completed commit, without a
/// catch — a transient failure (SQLITE_BUSY) flipped an already-completed job to Failed.
/// The completion must be atomic: index update inside the same commit as Completed, so a
/// failure rolls the whole completion back and the job re-runs from its checkpoint.
///
/// The other half of that finding — the retry subtracting RequiredBytesTarget from
/// FreeBytesLastKnown a second time — cannot happen at all since step 11b: completion no
/// longer does arithmetic on that column, which now only ever holds a measurement.
/// </summary>
[Trait("Category", "Platform")]
public sealed class JobCompletionAtomicityTests : IDisposable
{
    private const long InitialFreeBytes = 1_000_000;

    private readonly SqliteInMemoryContext _harness;
    private readonly Win32FileMover _mover;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public JobCompletionAtomicityTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

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
        _absRoot = Path.Combine(tempPath, $"ft-atomic-{Guid.NewGuid():N}");
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

    private JobExecutionEngine MakeEngine(IFileSearchIndex fts)
    {
        var ledger = Substitute.For<ISpaceLedger>();
        ledger.ReleaseAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);
        ledger.ReleaseInMemoryAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);

        var db = _harness.CreateContext();
        var indexUpdater = TestProjection.Index(db, fts);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db, TestProjection.Realtime());
        return new JobExecutionEngine(db, _mover, ledger, TestProjection.Space(db, ledger), indexUpdater, TestProjection.Overlay(db), notifications,
            TimeProvider.System, TestProjection.Realtime(), NullLogger<JobExecutionEngine>.Instance);
    }

    [Fact]
    public async Task Index_update_failing_once_never_flips_Completed_and_space_folds_exactly_once()
    {
        const string content = "atomic completion payload";
        var srcRel = R("src", "photo.jpg");
        var dstRel = R("dst", "photo.jpg");

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(dstRel), content); // already finalized — job checkpointed at DeletingSource

        int jobId;
        using (var db = _harness.CreateContext())
        {
            db.Volumes.Add(new Volume
            {
                Id = 1, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
                FreeBytesLastKnown = InitialFreeBytes, IsOnline = true,
            });
            db.Directories.Add(new DirectoryNode
            {
                Id = 1, VolumeId = 1, Name = "src", MaterializedPath = R("src"),
                IsMaterialized = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.Files.Add(new FileEntry
            {
                Id = 10, VolumeId = 1, DirectoryId = 1, Name = "photo.jpg", Extension = ".jpg",
                SizeBytes = content.Length, IsIncluded = true, IsPresent = true,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
            });
            var job = new OperationJob
            {
                Id = 1, Type = JobType.MoveFile, State = JobState.DeletingSource,
                IsIntraVolume = false, SourceVolumeId = 1, TargetVolumeId = 1,
                TargetRelativePath = dstRel, TotalBytes = content.Length,
                RequiredBytesTarget = content.Length, FreedBytesSource = 0,
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            };
            job.Items.Add(new OperationJobItem
            {
                FileId = 10,
                SourceRelativePath = srcRel, TargetRelativePath = dstRel,
                SizeBytes = content.Length, State = JobItemState.Verified,
                BytesCopied = content.Length,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        var fts = new FailOnceFileSearchIndex();

        // Attempt 1: the FTS upsert fails (transient SQLITE_BUSY analogue). The whole
        // completion must roll back — the job must NOT end up Failed (its files moved fine)
        // and must stay at a runnable checkpoint.
        await MakeEngine(fts).ExecuteJobAsync(jobId, CancellationToken.None);
        fts.FailedOnce.Should().BeTrue("the failure injection must actually have fired");

        using (var check = _harness.CreateContext())
        {
            var afterFirst = await check.OperationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            afterFirst.State.Should().NotBe(JobState.Failed,
                "a failed index update must not flip a physically-successful job to Failed");
        }

        // Attempt 2: the worker re-picks the job; the index update now succeeds.
        await MakeEngine(fts).ExecuteJobAsync(jobId, CancellationToken.None);

        using var final = _harness.CreateContext();
        var job2 = await final.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
        job2.State.Should().Be(JobState.Completed, $"error='{job2.ErrorMessage}'");
        job2.Items.Single().State.Should().Be(JobItemState.Done);

        // The free-space estimate is a measurement, not a running total: the completion (or its
        // retry) must not decrement it — the probe of the hard re-check is what writes it, and
        // here it reports exactly what the row already held.
        var volume = await final.Volumes.AsNoTracking().SingleAsync(v => v.Id == 1);
        volume.FreeBytesLastKnown.Should().Be(InitialFreeBytes);

        // The index update did land: the file row points at the target directory.
        var file = await final.Files.AsNoTracking().SingleAsync(f => f.Id == 10);
        var dir = await final.Directories.AsNoTracking().SingleAsync(d => d.Id == file.DirectoryId);
        dir.MaterializedPath.Should().Be(R("dst"));
    }

    [Fact]
    public async Task Persistently_failing_index_update_parks_the_job_Failed_instead_of_looping_forever()
    {
        // Review follow-up on #7: a PERSISTENT completion failure (FTS corruption, log volume
        // full) must not livelock the FIFO queue with an endless re-pick of the same job.
        // After a bounded number of attempts the job is parked Failed (visible, retryable).
        const string content = "persistent failure payload";
        var srcRel = R("src", "stuck.jpg");
        var dstRel = R("dst", "stuck.jpg");

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(dstRel), content);

        int jobId;
        using (var db = _harness.CreateContext())
        {
            db.Volumes.Add(new Volume
            {
                Id = 1, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
                FreeBytesLastKnown = InitialFreeBytes, IsOnline = true,
            });
            db.Directories.Add(new DirectoryNode
            {
                Id = 1, VolumeId = 1, Name = "src", MaterializedPath = R("src"),
                IsMaterialized = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.Files.Add(new FileEntry
            {
                Id = 10, VolumeId = 1, DirectoryId = 1, Name = "stuck.jpg", Extension = ".jpg",
                SizeBytes = content.Length, IsIncluded = true, IsPresent = true,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
            });
            var job = new OperationJob
            {
                Id = 1, Type = JobType.MoveFile, State = JobState.DeletingSource,
                IsIntraVolume = false, SourceVolumeId = 1, TargetVolumeId = 1,
                TargetRelativePath = dstRel, TotalBytes = content.Length,
                RequiredBytesTarget = content.Length, FreedBytesSource = 0,
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            };
            job.Items.Add(new OperationJobItem
            {
                FileId = 10,
                SourceRelativePath = srcRel, TargetRelativePath = dstRel,
                SizeBytes = content.Length, State = JobItemState.Verified,
                BytesCopied = content.Length,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        var fts = new AlwaysFailFileSearchIndex();

        // The worker would re-pick the runnable job after each rolled-back attempt; five
        // executions are more than the budget — the job must be parked Failed by then.
        for (int i = 0; i < 5; i++)
            await MakeEngine(fts).ExecuteJobAsync(jobId, CancellationToken.None);

        using var final = _harness.CreateContext();
        var job3 = await final.OperationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job3.State.Should().Be(JobState.Failed,
            "a persistent completion failure must park the job, not loop forever " +
            $"(state={job3.State}, error='{job3.ErrorMessage}')");
        job3.ErrorMessage.Should().NotBeNullOrEmpty();

        // Space never folded: every completion attempt rolled back, the final Failed does not fold.
        var volume = await final.Volumes.AsNoTracking().SingleAsync(v => v.Id == 1);
        volume.FreeBytesLastKnown.Should().Be(InitialFreeBytes);
    }

    /// <summary>Upsert always throws — the persistent-failure analogue.</summary>
    private sealed class AlwaysFailFileSearchIndex : IFileSearchIndex
    {
        public Task UpsertAsync(int fileId, string name, string path, CancellationToken ct)
            => throw new InvalidOperationException("simulated persistent index failure");

        public Task ClearVolumeAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
        public Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
        public Task RebuildAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct) => Task.CompletedTask;
        public Task SyncDirectoriesAsync(IReadOnlyCollection<int> directoryIds, CancellationToken ct) => Task.CompletedTask;
        public Task PruneVolumeAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAsync(int fileId, CancellationToken ct) => Task.CompletedTask;
        public Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct)
            => Task.FromResult(new PagedResult<int>([], 0, query.Skip, query.Take));
    }

    /// <summary>Throws on the first Upsert (transient-failure analogue), succeeds afterwards.</summary>
    private sealed class FailOnceFileSearchIndex : IFileSearchIndex
    {
        public bool FailedOnce { get; private set; }

        public Task UpsertAsync(int fileId, string name, string path, CancellationToken ct)
        {
            if (!FailedOnce)
            {
                FailedOnce = true;
                throw new InvalidOperationException("simulated transient failure (SQLITE_BUSY)");
            }
            return Task.CompletedTask;
        }

        public Task ClearVolumeAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
        public Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
        public Task RebuildAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct) => Task.CompletedTask;
        public Task SyncDirectoriesAsync(IReadOnlyCollection<int> directoryIds, CancellationToken ct) => Task.CompletedTask;
        public Task PruneVolumeAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAsync(int fileId, CancellationToken ct) => Task.CompletedTask;
        public Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct)
            => Task.FromResult(new PagedResult<int>([], 0, query.Skip, query.Take));
    }
}
