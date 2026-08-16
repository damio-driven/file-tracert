using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data.Entities;
using FileTracert.Platform;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// Review finding #5: the ledger release used to run in a transaction separate from the
/// terminal-state commit, so a crash in between left phantom IsActive reservations. The fix
/// deactivates the job's <c>SpaceLedgerEntries</c> in the SAME transaction as the terminal
/// state — these tests prove the DB rows flip together with the state, with the
/// <see cref="ISpaceLedger"/> singleton mocked to a no-op (only the in-memory mirror may
/// depend on it; durability must not).
/// </summary>
[Trait("Category", "Platform")]
public sealed class LedgerTerminalReleaseTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;
    private readonly Win32FileMover _mover;
    private readonly JobCancellationRegistry _registry = new();
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public LedgerTerminalReleaseTests()
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
        _absRoot = Path.Combine(tempPath, $"ft-ledgerrel-{Guid.NewGuid():N}");
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

    private JobExecutionEngine MakeEngine()
    {
        var db = _harness.CreateContext();
        var indexUpdater = TestProjection.Index(db);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db);
        return new JobExecutionEngine(db, _mover, NoopLedger(), indexUpdater, TestProjection.Overlay(db), notifications,
            TimeProvider.System, NullLogger<JobExecutionEngine>.Instance);
    }

    private int SeedCrossVolumeJob(JobState state, JobItemState itemState,
        string srcRel, string dstRel, int sizeBytes, string? tempPath = null)
    {
        using var db = _harness.CreateContext();
        db.Volumes.Add(new Volume
        {
            Id = 1, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
            FreeBytesLastKnown = 1_000_000, IsOnline = true,
        });
        var job = new OperationJob
        {
            Id = 1, Type = JobType.MoveFile, State = state,
            IsIntraVolume = false, SourceVolumeId = 1, TargetVolumeId = 1,
            TargetRelativePath = dstRel, TotalBytes = sizeBytes, RequiredBytesTarget = sizeBytes,
            SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        };
        job.Items.Add(new OperationJobItem
        {
            SourceRelativePath = srcRel, TargetRelativePath = dstRel,
            SizeBytes = sizeBytes, State = itemState, TempPath = tempPath,
            BytesCopied = itemState == JobItemState.Pending ? 0 : sizeBytes,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        });
        db.OperationJobs.Add(job);
        db.SpaceLedgerEntries.Add(new SpaceLedgerEntry
        {
            JobId = 1, VolumeId = 1, DeltaBytes = sizeBytes, IsActive = true,
        });
        db.SaveChanges();
        return job.Id;
    }

    private async Task<(JobState state, int activeEntries)> LoadOutcomeAsync(int jobId)
    {
        using var db = _harness.CreateContext();
        var state = await db.OperationJobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync();
        var active = await db.SpaceLedgerEntries.CountAsync(e => e.JobId == jobId && e.IsActive);
        return (state, active);
    }

    [Fact]
    public async Task Completed_deactivates_ledger_entries_in_the_same_commit()
    {
        const string content = "cross volume payload";
        var srcRel = R("src", "a.bin");
        var dstRel = R("dst", "a.bin");
        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(dstRel), content); // already finalized: engine goes straight to delete

        var jobId = SeedCrossVolumeJob(JobState.DeletingSource, JobItemState.Verified,
            srcRel, dstRel, content.Length);

        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        var (state, active) = await LoadOutcomeAsync(jobId);
        state.Should().Be(JobState.Completed);
        active.Should().Be(0,
            "the terminal commit itself must deactivate the reservation — a separate transaction leaves phantoms on crash");
    }

    [Fact]
    public async Task Failed_deactivates_ledger_entries_in_the_same_commit()
    {
        const string content = "cross volume payload";
        var srcRel = R("src", "b.bin");
        var dstRel = R("dst", "b.bin");
        var partialRel = dstRel + ".fadit-partial";
        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(partialRel), "wrong size"); // verify will fail → job Failed

        var jobId = SeedCrossVolumeJob(JobState.Verifying, JobItemState.Copied,
            srcRel, dstRel, content.Length, tempPath: partialRel);

        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        var (state, active) = await LoadOutcomeAsync(jobId);
        state.Should().Be(JobState.Failed);
        active.Should().Be(0, "a Failed commit must release the reservation durably");
    }

    [Fact]
    public async Task Cancel_deactivates_ledger_entries_in_the_same_commit()
    {
        var jobId = SeedCrossVolumeJob(JobState.Pending, JobItemState.Pending,
            R("src", "c.bin"), R("dst", "c.bin"), 100);

        var db = _harness.CreateContext();
        var ledger = NoopLedger();
        var queue = new QueueService(db, ledger, _registry,
            Substitute.For<FileTracert.Contracts.Platform.IFileMover>(),
            new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db),
            TestProjection.Unblocker(db),
            TestProjection.Revaluator(db, ledger),
            NullLogger<QueueService>.Instance);

        await queue.CancelAsync(jobId, CancellationToken.None);

        var (state, active) = await LoadOutcomeAsync(jobId);
        state.Should().Be(JobState.Cancelled);
        active.Should().Be(0, "a Cancelled commit must release the reservation durably");
    }
}
