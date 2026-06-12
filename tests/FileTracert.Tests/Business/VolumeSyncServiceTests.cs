using FileTracert.Business.Volumes;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

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
            await new VolumeSyncService(probe, ctx).SyncAsync(CancellationToken.None);
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
}
