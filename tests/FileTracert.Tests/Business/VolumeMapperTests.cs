using System.IO;
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
        long free = 400,
        DriveType driveType = DriveType.Fixed,
        bool? hasPhysicalExtents = true) =>
        new(
            VolumeGuid: @"\\?\Volume{11111111-1111-1111-1111-111111111111}\",
            SerialNumber: "ABCD-1234",
            Label: "Data",
            FileSystem: fileSystem,
            IsRemovable: driveType == DriveType.Removable,
            MountPoints: mountPoints ?? [@"E:\"],
            CapacityBytes: capacity,
            FreeBytes: free,
            PhysicalDiskId: @"\\.\PHYSICALDRIVE1",
            DriveType: driveType,
            HasPhysicalExtents: hasPhysicalExtents);

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

    [Fact]
    public void MapNew_classifies_and_sets_default_catalogable()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var fixedVol = VolumeMapper.MapNew(Probed(driveType: DriveType.Fixed), now);
        fixedVol.Kind.Should().Be(VolumeKind.Fixed);
        fixedVol.IsCatalogable.Should().BeTrue();

        var cloud = VolumeMapper.MapNew(Probed(driveType: DriveType.Fixed, hasPhysicalExtents: false), now);
        cloud.Kind.Should().Be(VolumeKind.Cloud);
        cloud.IsCatalogable.Should().BeFalse();
    }

    [Fact]
    public void ApplyLiveState_reconciles_kind_and_derives_catalogable_when_not_overridden()
    {
        // Existing row from before classification existed: Unknown + the migration default (catalogable).
        var existing = new Volume
        {
            VolumeGuid = "g", FileSystem = "NTFS", ScanEngine = VolumeScanEngine.UsnJournal,
            Kind = VolumeKind.Unknown, IsCatalogable = true,
        };
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // Probe now reports a cloud mount (no physical extents).
        VolumeMapper.ApplyLiveState(existing, Probed(hasPhysicalExtents: false), now);

        existing.Kind.Should().Be(VolumeKind.Cloud);
        existing.IsCatalogable.Should().BeFalse("default catalogable for a cloud volume is false");
    }

    // ---- ApplyOfflineReclassification ----

    [Fact]
    public void OfflineReclassify_unknown_with_null_disk_and_not_removable_becomes_cloud()
    {
        // Google Drive offline: Kind=Unknown, PhysicalDiskId=null, IsRemovable=false.
        var volume = new Volume
        {
            VolumeGuid = "g", FileSystem = "NTFS", Kind = VolumeKind.Unknown,
            IsCatalogable = true, PhysicalDiskId = null, IsRemovable = false,
        };

        VolumeMapper.ApplyOfflineReclassification(volume);

        volume.Kind.Should().Be(VolumeKind.Cloud);
        volume.IsCatalogable.Should().BeFalse();
    }

    [Fact]
    public void OfflineReclassify_unknown_removable_drive_stays_unknown()
    {
        // A removable USB drive whose WMI lookup transiently failed must not become Cloud.
        var volume = new Volume
        {
            VolumeGuid = "g", FileSystem = "exFAT", Kind = VolumeKind.Unknown,
            IsCatalogable = true, PhysicalDiskId = null, IsRemovable = true,
        };

        VolumeMapper.ApplyOfflineReclassification(volume);

        volume.Kind.Should().Be(VolumeKind.Unknown);
        volume.IsCatalogable.Should().BeTrue();
    }

    [Fact]
    public void OfflineReclassify_unknown_with_physical_disk_stays_unknown()
    {
        // A real disk that happened to get Kind=Unknown (e.g. before classification existed).
        var volume = new Volume
        {
            VolumeGuid = "g", FileSystem = "NTFS", Kind = VolumeKind.Unknown,
            IsCatalogable = true, PhysicalDiskId = @"\\.\PHYSICALDRIVE1", IsRemovable = false,
        };

        VolumeMapper.ApplyOfflineReclassification(volume);

        volume.Kind.Should().Be(VolumeKind.Unknown);
        volume.IsCatalogable.Should().BeTrue();
    }

    [Fact]
    public void OfflineReclassify_already_classified_volume_is_untouched()
    {
        var volume = new Volume
        {
            VolumeGuid = "g", FileSystem = "NTFS", Kind = VolumeKind.Fixed,
            IsCatalogable = true, PhysicalDiskId = @"\\.\PHYSICALDRIVE0", IsRemovable = false,
        };

        VolumeMapper.ApplyOfflineReclassification(volume);

        volume.Kind.Should().Be(VolumeKind.Fixed);
    }

    [Fact]
    public void OfflineReclassify_preserves_manual_catalogable_override()
    {
        // User manually excluded an Unknown volume (IsCatalogable=false differs from Unknown default=true).
        var volume = new Volume
        {
            VolumeGuid = "g", FileSystem = "NTFS", Kind = VolumeKind.Unknown,
            IsCatalogable = false, PhysicalDiskId = null, IsRemovable = false,
        };

        VolumeMapper.ApplyOfflineReclassification(volume);

        // Kind reclassified to Cloud, but user's false override is preserved (Cloud default is also false).
        volume.Kind.Should().Be(VolumeKind.Cloud);
        volume.IsCatalogable.Should().BeFalse();
    }

    [Fact]
    public void ApplyLiveState_preserves_manual_catalogable_override()
    {
        // User re-enabled a cloud volume (false positive): IsCatalogable=true differs from the
        // cloud default (false), so the sync must not clobber it.
        var reEnabledCloud = new Volume
        {
            VolumeGuid = "g", FileSystem = "NTFS", Kind = VolumeKind.Cloud, IsCatalogable = true,
        };
        VolumeMapper.ApplyLiveState(reEnabledCloud, Probed(hasPhysicalExtents: false), DateTime.UtcNow);
        reEnabledCloud.IsCatalogable.Should().BeTrue();

        // User excluded a real fixed disk: IsCatalogable=false differs from the fixed default (true).
        var excludedFixed = new Volume
        {
            VolumeGuid = "g", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = false,
        };
        VolumeMapper.ApplyLiveState(excludedFixed, Probed(driveType: DriveType.Fixed), DateTime.UtcNow);
        excludedFixed.IsCatalogable.Should().BeFalse();
    }
}
