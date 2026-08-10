using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Business;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Host;

public sealed class VolumeSyncWorkerTests
{
    private const string StaleGuid = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string LiveGuid = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\";

    [Fact]
    public async Task Worker_marks_missing_volume_offline_and_adds_probed_one()
    {
        using var factory = new FileTracertAppFactory
        {
            DisableScan = true,
            Probe = new FakeVolumesProbe(
            [
                new ProbedVolume(LiveGuid, "SER", "Live", "NTFS", IsRemovable: false,
                    MountPoints: [@"X:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null),
            ]),
            Seed = async (db, ct) =>
            {
                // Previously known volume that the probe no longer reports.
                db.Volumes.Add(new Volume
                {
                    VolumeGuid = StaleGuid,
                    FileSystem = "NTFS",
                    ScanEngine = VolumeScanEngine.UsnJournal,
                    IsOnline = true,
                });
                await db.SaveChangesAsync(ct);
            },
        };

        using var _ = factory.CreateClient();

        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var live = await db.Volumes.SingleOrDefaultAsync(v => v.VolumeGuid == LiveGuid);
            var stale = await db.Volumes.SingleOrDefaultAsync(v => v.VolumeGuid == StaleGuid);
            return live is { IsOnline: true } && stale is { IsOnline: false };
        });

        using var read = factory.Services.CreateScope();
        var ctx = read.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        (await ctx.Volumes.CountAsync()).Should().Be(2);
        var liveVolume = await ctx.Volumes.SingleAsync(v => v.VolumeGuid == LiveGuid);
        liveVolume.Label.Should().Be("Live");
        liveVolume.LastDriveLetter.Should().Be("X:");
    }

    /// <summary>
    /// FIX #13 end-to-end through the real host wiring: the drive comes back, the sync notices the
    /// offline→online transition and the job parked on that volume returns to Pending by itself —
    /// nobody touched the queue. The queue worker is out of the way so the assertion observes the
    /// revaluation, not the execution that follows it.
    /// </summary>
    [Fact]
    public async Task Volume_coming_back_online_revaluates_the_jobs_parked_on_it()
    {
        using var factory = new FileTracertAppFactory
        {
            DisableScan = true,
            DisableQueue = true,
            Probe = new FakeVolumesProbe(
            [
                new ProbedVolume(LiveGuid, "SER", "Live", "NTFS", IsRemovable: false,
                    MountPoints: [@"X:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null),
            ]),
            Seed = async (db, ct) =>
            {
                // The volume is offline at startup; the probe above will bring it back.
                var volume = new Volume
                {
                    VolumeGuid = LiveGuid,
                    FileSystem = "NTFS",
                    ScanEngine = VolumeScanEngine.UsnJournal,
                    IsOnline = false,
                };
                db.Volumes.Add(volume);
                await db.SaveChangesAsync(ct);

                db.OperationJobs.Add(new OperationJob
                {
                    Type = JobType.CreateFolder,
                    State = JobState.Blocked,
                    BlockReason = JobBlockReason.TargetVolumeOffline,
                    IsIntraVolume = true,
                    TargetVolumeId = volume.Id,
                    TargetRelativePath = @"Archivio\Nuova",
                    SequenceOrder = 1,
                    ErrorMessage = "volume scollegato",
                });
                await db.SaveChangesAsync(ct);
            },
        };

        using var _ = factory.CreateClient();

        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var job = await db.OperationJobs.AsNoTracking().SingleOrDefaultAsync();
            return job is { State: JobState.Pending, BlockReason: JobBlockReason.None };
        });
    }
}
