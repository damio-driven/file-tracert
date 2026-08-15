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
/// Review finding C20: an intra-volume rename/move onto an existing target used to surface
/// as a raw IOException → Failed terminal. The spec (§4/§6) wants the typed
/// <c>NameCollisionException</c> → <c>Blocked(NameCollision)</c>, reactivatable — the same
/// mapping FinalizePartial already had. Real mover, real SQLite.
/// </summary>
[Trait("Category", "Platform")]
public sealed class IntraVolumeCollisionTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;
    private readonly Win32FileMover _mover;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public IntraVolumeCollisionTests()
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
        _absRoot = Path.Combine(tempPath, $"ft-collide-{Guid.NewGuid():N}");
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
        ledger.ReleaseInMemoryAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);

        var db = _harness.CreateContext();
        var indexUpdater = TestProjection.Index(db);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db);
        return new JobExecutionEngine(db, _mover, ledger, indexUpdater, TestProjection.Overlay(db), notifications,
            TimeProvider.System, NullLogger<JobExecutionEngine>.Instance);
    }

    private int SeedIntraMoveJob(string srcRel, string dstRel, int sizeBytes)
    {
        using var db = _harness.CreateContext();
        db.Volumes.Add(new Volume
        {
            Id = 1, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
            FreeBytesLastKnown = 1_000_000, IsOnline = true,
        });
        var job = new OperationJob
        {
            Id = 1, Type = JobType.MoveFile, State = JobState.Pending,
            IsIntraVolume = true, SourceVolumeId = 1, TargetVolumeId = 1,
            TargetRelativePath = dstRel,
            SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        };
        job.Items.Add(new OperationJobItem
        {
            SourceRelativePath = srcRel, TargetRelativePath = dstRel,
            SizeBytes = sizeBytes, State = JobItemState.Pending,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        });
        db.OperationJobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    [Fact]
    public async Task Case_only_rename_is_not_a_collision_and_completes()
    {
        // Review follow-up on C20: on case-insensitive NTFS, File.Exists(dest) is true for the
        // SOURCE ITSELF when the rename differs only by case ("report.txt" → "REPORT.txt").
        // That must stay a normal, working rename — not a permanent Blocked(NameCollision).
        const string content = "same file, new casing";
        var srcRel = R("docs", "report.txt");
        Directory.CreateDirectory(Abs(R("docs")));
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
                Id = 1, Type = JobType.RenameFile, State = JobState.Pending,
                IsIntraVolume = true, SourceVolumeId = 1, TargetVolumeId = 1,
                TargetRelativePath = "REPORT.txt",
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            };
            job.Items.Add(new OperationJobItem
            {
                SourceRelativePath = srcRel, TargetRelativePath = R("docs", "REPORT.txt"),
                SizeBytes = content.Length, State = JobItemState.Pending,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
            jobId = job.Id;
        }

        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        using var check = _harness.CreateContext();
        var job2 = await check.OperationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job2.State.Should().Be(JobState.Completed,
            $"a case-only rename is not a collision (block={job2.BlockReason}, error='{job2.ErrorMessage}')");

        var actualName = new DirectoryInfo(Abs(R("docs"))).GetFiles().Single().Name;
        actualName.Should().Be("REPORT.txt", "the new casing must be applied on disk");
    }

    [Fact]
    public async Task Intra_volume_move_onto_existing_target_blocks_as_NameCollision_not_Failed()
    {
        const string sourceContent = "the file being moved";
        const string occupantContent = "someone is already here";
        var srcRel = R("from", "report.txt");
        var dstRel = R("to", "report.txt");

        Directory.CreateDirectory(Abs(R("from")));
        File.WriteAllText(Abs(srcRel), sourceContent);
        Directory.CreateDirectory(Abs(R("to")));
        File.WriteAllText(Abs(dstRel), occupantContent);

        var jobId = SeedIntraMoveJob(srcRel, dstRel, sourceContent.Length);

        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        using var check = _harness.CreateContext();
        var job = await check.OperationJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.State.Should().Be(JobState.Blocked,
            $"a name collision is a reactivatable block, not a terminal failure (error='{job.ErrorMessage}')");
        job.BlockReason.Should().Be(JobBlockReason.NameCollision);

        // Nothing was touched: both files intact.
        File.ReadAllText(Abs(srcRel)).Should().Be(sourceContent);
        File.ReadAllText(Abs(dstRel)).Should().Be(occupantContent);
    }
}
