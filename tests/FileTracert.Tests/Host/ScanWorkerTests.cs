using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Business;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Host;

public sealed class ScanWorkerTests
{
    private const string Guid = @"\\?\Volume{33333333-3333-3333-3333-333333333333}\";
    private static readonly DateTime T = new(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Worker_scans_never_scanned_volume_then_leaves_it_alone()
    {
        var entries = new List<ScanEntry>
        {
            new(@"Media", "Media", true, 0, T, T, FileAttributes.Directory),
            new(@"Media\a.jpg", "a.jpg", false, 11, T, T, FileAttributes.Normal),
            new(@"Media\b.png", "b.png", false, 22, T, T, FileAttributes.Normal),
        };

        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            Probe = new FakeVolumesProbe(
            [
                new ProbedVolume(Guid, "SER", "Disk", "exFAT", IsRemovable: false,
                    MountPoints: [@"X:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null),
            ]),
            DirectoryEnumerator = new FakeDirectoryEnumerator(entries),
            Seed = async (db, ct) =>
            {
                var volume = new Volume
                {
                    VolumeGuid = Guid,
                    FileSystem = "exFAT",
                    ScanEngine = VolumeScanEngine.Enumeration,
                    IsOnline = true,
                };
                db.Volumes.Add(volume);
                await db.SaveChangesAsync(ct);

                db.WatchedRoots.Add(new WatchedRoot { VolumeId = volume.Id, RelativePath = "", IsActive = true });
                await db.SaveChangesAsync(ct);
            },
        };

        using var _ = factory.CreateClient();

        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var volume = await db.Volumes.SingleAsync();
            return volume.LastFullScanUtc is not null;
        });

        DateTime scannedAt;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var files = await db.Files.Select(f => f.Name).ToListAsync();
            files.Should().BeEquivalentTo("a.jpg", "b.png");
            scannedAt = (await db.Volumes.SingleAsync()).LastFullScanUtc!.Value;
        }

        // Already-scanned volume must not be picked up again by the poll.
        await Task.Delay(1500);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            (await db.Volumes.SingleAsync()).LastFullScanUtc!.Value.Should().Be(scannedAt);
            (await db.Files.CountAsync()).Should().Be(2);
        }
    }

    [Fact]
    public async Task Failed_scan_raises_a_notification_and_the_worker_keeps_running()
    {
        // The volume is marked online and never scanned, but the probe doesn't know it
        // → ScanService throws "offline". The worker must record an Error notification
        // and survive (not crash the loop).
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            Probe = new FakeVolumesProbe([]),
            Seed = async (db, ct) =>
            {
                var volume = new Volume
                {
                    VolumeGuid = Guid,
                    Label = "Broken",
                    FileSystem = "exFAT",
                    ScanEngine = VolumeScanEngine.Enumeration,
                    IsOnline = true,
                };
                db.Volumes.Add(volume);
                await db.SaveChangesAsync(ct);

                db.WatchedRoots.Add(new WatchedRoot { VolumeId = volume.Id, RelativePath = "", IsActive = true });
                await db.SaveChangesAsync(ct);
            },
        };

        using var _ = factory.CreateClient();

        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            return await db.Notifications.AnyAsync(n => n.Source == "Scan");
        });

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        var notification = await verifyDb.Notifications.SingleAsync(n => n.Source == "Scan");
        notification.Severity.Should().Be(NotificationSeverity.Error);
        notification.VolumeId.Should().NotBeNull();
        notification.Message.Should().NotBeNullOrWhiteSpace();
    }
}
