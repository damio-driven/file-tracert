using FileTracert.Business.Volumes;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Business;

public class VolumeMapperTests
{
    private static ProbedVolume Probed(
        string fileSystem = "NTFS",
        IReadOnlyList<string>? mountPoints = null,
        long capacity = 1000,
        long free = 400) =>
        new(
            VolumeGuid: @"\\?\Volume{11111111-1111-1111-1111-111111111111}\",
            SerialNumber: "ABCD-1234",
            Label: "Data",
            FileSystem: fileSystem,
            IsRemovable: false,
            MountPoints: mountPoints ?? [@"E:\"],
            CapacityBytes: capacity,
            FreeBytes: free,
            PhysicalDiskId: @"\\.\PHYSICALDRIVE1");

    [Theory]
    [InlineData("NTFS", VolumeScanEngine.UsnJournal)]
    [InlineData("ntfs", VolumeScanEngine.UsnJournal)]
    [InlineData("exFAT", VolumeScanEngine.Enumeration)]
    [InlineData("FAT32", VolumeScanEngine.Enumeration)]
    public void EngineFor_picks_usn_only_for_ntfs(string fs, VolumeScanEngine expected)
    {
        VolumeMapper.EngineFor(fs).Should().Be(expected);
    }

    [Fact]
    public void DriveLetterOf_takes_first_mount_point_letter()
    {
        VolumeMapper.DriveLetterOf(Probed(mountPoints: [@"e:\", @"F:\"])).Should().Be("E:");
        VolumeMapper.DriveLetterOf(Probed(mountPoints: [])).Should().BeNull();
    }

    [Fact]
    public void MapNew_fills_live_state_and_derived_engine()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var volume = VolumeMapper.MapNew(Probed(), now);

        volume.VolumeGuid.Should().Be(@"\\?\Volume{11111111-1111-1111-1111-111111111111}\");
        volume.ScanEngine.Should().Be(VolumeScanEngine.UsnJournal);
        volume.IsOnline.Should().BeTrue();
        volume.LastSeenUtc.Should().Be(now);
        volume.LastDriveLetter.Should().Be("E:");
        volume.CapacityBytes.Should().Be(1000);
        volume.FreeBytesLastKnown.Should().Be(400);
        volume.LastUsn.Should().BeNull();
        volume.LastFullScanUtc.Should().BeNull();
    }

    [Fact]
    public void ApplyLiveState_refreshes_live_without_touching_checkpoints()
    {
        var existing = new Volume
        {
            VolumeGuid = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
            LastUsn = 9999,
            LastFullScanUtc = new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc),
            CapacityBytes = 10,
            FreeBytesLastKnown = 1,
            IsOnline = false,
        };
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        VolumeMapper.ApplyLiveState(existing, Probed(capacity: 2000, free: 800), now);

        existing.CapacityBytes.Should().Be(2000);
        existing.FreeBytesLastKnown.Should().Be(800);
        existing.IsOnline.Should().BeTrue();
        existing.LastSeenUtc.Should().Be(now);

        // Checkpoints and engine must be preserved.
        existing.LastUsn.Should().Be(9999);
        existing.LastFullScanUtc.Should().Be(new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc));
        existing.ScanEngine.Should().Be(VolumeScanEngine.UsnJournal);
    }
}
