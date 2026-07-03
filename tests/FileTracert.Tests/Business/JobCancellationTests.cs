using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Platform;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// Reproduces the "cancel lost, source trashed anyway" bug against the REAL <see cref="Win32FileMover"/>
/// and real DbContexts. The queue runs the job on one DbContext while <c>CancelAsync</c> writes
/// <c>Cancelled</c> on another; the engine must re-read the committed state before any destructive
/// step and leave the source untouched. No mock of the engine/queue under test.
/// </summary>
[Trait("Category", "Platform")]
public sealed class JobCancellationTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;
    private readonly Win32FileMover _mover;
    private readonly JobCancellationRegistry _registry = new();
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public JobCancellationTests()
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
        _absRoot = Path.Combine(tempPath, $"ft-cancel-{Guid.NewGuid():N}");
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

    private static ISpaceLedger NoopLedger()
    {
        var ledger = Substitute.For<ISpaceLedger>();
        ledger.ReleaseAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);
        return ledger;
    }

    private JobExecutionEngine MakeEngine(IFileMover mover)
    {
        var db = _harness.CreateContext();
        var indexUpdater = new IndexUpdater(db, new FakeFileSearchIndex(), NullLogger<IndexUpdater>.Instance);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db);
        return new JobExecutionEngine(db, mover, NoopLedger(), indexUpdater, notifications,
            TimeProvider.System, NullLogger<JobExecutionEngine>.Instance);
    }

    private QueueService MakeQueue() =>
        new(_harness.CreateContext(), NoopLedger(), _registry, Substitute.For<IFileMover>(),
            NullLogger<QueueService>.Instance);

    [Fact]
    public async Task Cancel_during_execution_never_recycles_the_source()
    {
        const string content = "precious source data";
        var srcRel = R("src", "keep.txt");
        var dstRel = R("dst", "keep.txt");
        var partialRel = dstRel + ".fadit-partial";

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);

        int jobId;
        using (var db = _harness.CreateContext())
        {
            db.Volumes.Add(new Volume
            {
                Id = 1, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
                FreeBytesLastKnown = 1_000_000, IsOnline = true,
            });
            var job = new OperationJob
            {
                Id = 1, Type = JobType.MoveFile, State = JobState.Copying,
                IsIntraVolume = false, SourceVolumeId = 1, TargetVolumeId = 1,
                TargetRelativePath = dstRel, TotalBytes = content.Length, RequiredBytesTarget = content.Length,
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            };
            job.Items.Add(new OperationJobItem
            {
                SourceRelativePath = srcRel, TargetRelativePath = dstRel,
                SizeBytes = content.Length, State = JobItemState.Pending,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        // Gate the mover at Verify: the copy has completed (partial on disk), the engine is one
        // step short of finalize/delete. That is exactly the window where a cancel must still win.
        var gated = new VerifyGatedMover(_mover);
        var engine = MakeEngine(gated);

        var exec = engine.ExecuteJobAsync(jobId, CancellationToken.None);

        await gated.VerifyReached;
        await MakeQueue().CancelAsync(jobId, CancellationToken.None); // writes Cancelled on another DbContext
        gated.Proceed();

        await exec;

        // The source must be exactly where it was — never sent to the recycle bin.
        File.Exists(Abs(srcRel)).Should().BeTrue();
        File.ReadAllText(Abs(srcRel)).Should().Be(content);

        // The job stays Cancelled (the engine did not overwrite it with Completed).
        using var check = _harness.CreateContext();
        var state = await check.OperationJobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync();
        state.Should().Be(JobState.Cancelled);

        // No orphan partial left behind.
        File.Exists(Abs(partialRel)).Should().BeFalse();
    }

    /// <summary>Real mover wrapper that blocks inside <see cref="Verify"/> until the test releases it.</summary>
    private sealed class VerifyGatedMover : IFileMover
    {
        private readonly IFileMover _inner;
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _proceed = new(0, 1);

        public VerifyGatedMover(IFileMover inner) => _inner = inner;

        public Task VerifyReached => _reached.Task;
        public void Proceed() => _proceed.Release();

        public bool Verify(string aVolGuid, string aRel, string bVolGuid, string bRel, bool withHash)
        {
            _reached.TrySetResult();
            _proceed.Wait();
            return _inner.Verify(aVolGuid, aRel, bVolGuid, bRel, withHash);
        }

        public void CreateFolder(string v, string r) => _inner.CreateFolder(v, r);
        public void RenameIntraVolume(string v, string r, string n) => _inner.RenameIntraVolume(v, r, n);
        public void MoveIntraVolume(string v, string s, string d) => _inner.MoveIntraVolume(v, s, d);
        public Task CopyFileAsync(string sv, string sr, string dv, string dpr, Func<long, CancellationToken, Task>? p, CancellationToken ct)
            => _inner.CopyFileAsync(sv, sr, dv, dpr, p, ct);
        public void FinalizePartial(string v, string p, string f) => _inner.FinalizePartial(v, p, f);
        public void DeleteToRecycleBin(string v, string r) => _inner.DeleteToRecycleBin(v, r);
        public void EnsureTargetDirectory(string v, string r) => _inner.EnsureTargetDirectory(v, r);
    }
}
