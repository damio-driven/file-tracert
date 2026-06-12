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
}
