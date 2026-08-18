using FileTracert.Business.Volumes;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

public sealed class VolumeSyncServiceTests
{
    private static ProbedVolume Probed(string guid, string fs = "NTFS", long free = 100) => new(
        guid, "SER", "Label", fs, IsRemovable: false,
        MountPoints: [@"Z:\"], CapacityBytes: 1000, FreeBytes: free, PhysicalDiskId: null);

    [Fact]
    public async Task Sync_inserts_new_updates_live_and_marks_missing_offline()
    {
        using var harness = new SqliteInMemoryContext();

        // Seed: A (will stay present), C (will be missing → offline).
        await using (var seed = harness.CreateContext())
        {
            seed.Volumes.Add(new Volume
            {
                VolumeGuid = "A", FileSystem = "NTFS", ScanEngine = VolumeScanEngine.UsnJournal,
                IsOnline = true, FreeBytesLastKnown = 10, LastUsn = 500,
                LastFullScanUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.Volumes.Add(new Volume
            {
                VolumeGuid = "C", FileSystem = "NTFS", ScanEngine = VolumeScanEngine.UsnJournal, IsOnline = true,
            });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = harness.CreateContext())
        {
            var probe = new FakeVolumesProbe([Probed("A", free: 777), Probed("B", fs: "exFAT")]);
            await new VolumeSyncService(probe, ctx, TestProjection.Realtime(), NullLogger<VolumeSyncService>.Instance).SyncAsync(CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var a = await read.Volumes.SingleAsync(v => v.VolumeGuid == "A");
        var b = await read.Volumes.SingleAsync(v => v.VolumeGuid == "B");
        var c = await read.Volumes.SingleAsync(v => v.VolumeGuid == "C");

        // A: live state refreshed, checkpoint preserved.
        a.FreeBytesLastKnown.Should().Be(777);
        a.IsOnline.Should().BeTrue();
        a.LastUsn.Should().Be(500);
        a.LastFullScanUtc.Should().NotBeNull();

        // B: newly inserted with derived engine.
        b.ScanEngine.Should().Be(VolumeScanEngine.Enumeration);
        b.IsOnline.Should().BeTrue();

        // C: missing from the probe → offline, not deleted.
        c.IsOnline.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_reports_the_volumes_that_came_back_online()
    {
        using var harness = new SqliteInMemoryContext();

        await using (var seed = harness.CreateContext())
        {
            // "Back": known but offline, and the probe sees it again → the mount event the queue waits for.
            seed.Volumes.Add(new Volume { VolumeGuid = "Back", FileSystem = "NTFS", IsOnline = false });
            // "Steady": already online, nothing changed → must NOT trigger a revaluation.
            seed.Volumes.Add(new Volume { VolumeGuid = "Steady", FileSystem = "NTFS", IsOnline = true });
            await seed.SaveChangesAsync();
        }

        IReadOnlyList<int> cameOnline;
        await using (var ctx = harness.CreateContext())
        {
            var probe = new FakeVolumesProbe([Probed("Back"), Probed("Steady"), Probed("BrandNew")]);
            cameOnline = await new VolumeSyncService(probe, ctx, TestProjection.Realtime(), NullLogger<VolumeSyncService>.Instance)
                .SyncAsync(CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var back = await read.Volumes.SingleAsync(v => v.VolumeGuid == "Back");

        cameOnline.Should().Equal([back.Id],
            "only an offline→online transition of a KNOWN volume can resurrect jobs parked on it");
    }

    [Fact]
    public async Task Sync_reclassifies_offline_unknown_cloud_drive_using_persisted_data()
    {
        using var harness = new SqliteInMemoryContext();

        // Seed: a volume that was previously classified Unknown (e.g. before the cloud-detection
        // fix landed) with no physical disk topology → should be reclassified to Cloud at next sync.
        await using (var seed = harness.CreateContext())
        {
            seed.Volumes.Add(new Volume
            {
                VolumeGuid = "GDrive", FileSystem = "FAT32", ScanEngine = VolumeScanEngine.Enumeration,
                Kind = VolumeKind.Unknown, IsCatalogable = true,
                PhysicalDiskId = null, IsRemovable = false, IsOnline = false,
            });
            await seed.SaveChangesAsync();
        }

        // Probe returns nothing — the cloud drive stays offline.
        await using (var ctx = harness.CreateContext())
        {
            await new VolumeSyncService(new FakeVolumesProbe([]), ctx, TestProjection.Realtime(), NullLogger<VolumeSyncService>.Instance)
                .SyncAsync(CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var v = await read.Volumes.SingleAsync(v => v.VolumeGuid == "GDrive");

        v.Kind.Should().Be(VolumeKind.Cloud);
        v.IsCatalogable.Should().BeFalse();
        v.IsOnline.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_does_not_reclassify_offline_unknown_volume_with_physical_disk()
    {
        using var harness = new SqliteInMemoryContext();

        await using (var seed = harness.CreateContext())
        {
            seed.Volumes.Add(new Volume
            {
                VolumeGuid = "RealDisk", FileSystem = "NTFS", ScanEngine = VolumeScanEngine.UsnJournal,
                Kind = VolumeKind.Unknown, IsCatalogable = true,
                PhysicalDiskId = @"\\.\PHYSICALDRIVE2", IsRemovable = false, IsOnline = false,
            });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = harness.CreateContext())
        {
            await new VolumeSyncService(new FakeVolumesProbe([]), ctx, TestProjection.Realtime(), NullLogger<VolumeSyncService>.Instance)
                .SyncAsync(CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var v = await read.Volumes.SingleAsync(v => v.VolumeGuid == "RealDisk");

        v.Kind.Should().Be(VolumeKind.Unknown, "a real disk in WMI topology must not become Cloud");
        v.IsCatalogable.Should().BeTrue();
    }
}
