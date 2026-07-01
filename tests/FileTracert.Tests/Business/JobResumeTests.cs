using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
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
/// Resume tests for <see cref="JobExecutionEngine"/> against the REAL <see cref="Win32FileMover"/>
/// on temp directories (no mover mock — the resume path is what's under test). Reproduces the
/// stall bug: an item left in <c>Copying</c> by an interrupted run must be re-copied, not skipped
/// forever. Cross-volume is simulated on one physical volume (same GUID, IsIntraVolume=false),
/// which drives the full copy → verify → delete state machine.
/// </summary>
[Trait("Category", "Platform")]
public sealed class JobResumeTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;
    private readonly Win32FileMover _mover;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public JobResumeTests()
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
        _absRoot = Path.Combine(tempPath, $"ft-resume-{Guid.NewGuid():N}");
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

    private JobExecutionEngine MakeEngine()
    {
        var ledger = Substitute.For<ISpaceLedger>();
        ledger.ReleaseAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);

        var db = _harness.CreateContext();
        var indexUpdater = new IndexUpdater(db, new FakeFileSearchIndex(), NullLogger<IndexUpdater>.Instance);
        return new JobExecutionEngine(db, _mover, ledger, indexUpdater, NullLogger<JobExecutionEngine>.Instance);
    }

    [Fact]
    public async Task Resume_recopies_interrupted_Copying_item_and_completes_job()
    {
        const string content = "the real content"; // 16 bytes
        var srcRel = R("src", "report.txt");
        var dstRel = R("dst", "report.txt");
        var partialRel = dstRel + ".fadit-partial";

        // Physical layout left by a killed copy: source intact, orphan partial half-written.
        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(partialRel), "half");

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
                SizeBytes = content.Length, State = JobItemState.Copying,
                TempPath = partialRel, BytesCopied = 4,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        // No stall: the job advanced through the whole machine.
        using var check = _harness.CreateContext();
        var state = await check.OperationJobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync();
        state.Should().Be(JobState.Completed);

        // Destination holds the full, correct content; the orphan partial is gone.
        File.Exists(Abs(dstRel)).Should().BeTrue();
        File.ReadAllText(Abs(dstRel)).Should().Be(content);
        File.Exists(Abs(partialRel)).Should().BeFalse();
    }
}
