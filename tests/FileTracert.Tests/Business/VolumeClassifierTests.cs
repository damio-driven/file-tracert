using System.IO;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FluentAssertions;

namespace FileTracert.Tests.Business;

public sealed class VolumeClassifierTests
{
    private static ProbedVolume Probed(
        DriveType driveType = DriveType.Fixed,
        bool hasPhysicalExtents = true,
        string? partitionTypeGuid = null,
        string? label = null,
        IReadOnlyList<string>? mountPoints = null,
        string fileSystem = "NTFS",
        long capacityBytes = 500L * 1024 * 1024 * 1024) => new(
        VolumeGuid: @"\\?\Volume{11111111-1111-1111-1111-111111111111}\",
        SerialNumber: "SER",
        Label: label,
        FileSystem: fileSystem,
        IsRemovable: driveType == DriveType.Removable,
        MountPoints: mountPoints ?? [@"E:\"],
        CapacityBytes: capacityBytes,
        FreeBytes: 0,
        PhysicalDiskId: null,
        DriveType: driveType,
        HasPhysicalExtents: hasPhysicalExtents,
        PartitionTypeGuid: partitionTypeGuid);

    [Fact]
    public void No_physical_extents_is_cloud()
    {
        // Google Drive File Stream / OneDrive mount: a letter, but no disk extents.
        var probed = Probed(driveType: DriveType.Fixed, hasPhysicalExtents: false, mountPoints: [@"G:\"]);

        VolumeClassifier.Classify(probed).Should().Be(VolumeKind.Cloud);
    }

    [Theory]
    [InlineData("c12a7328-f81f-11d2-ba4b-00a0c93ec93b")] // ESP
    [InlineData("E3C9E316-0B5C-4DB8-817D-F92DF00215AE")] // MSR (mixed case)
    [InlineData("de94bba4-06d1-4d40-a16a-bfd50179d6ac")] // WinRE
    public void Known_system_partition_guid_is_system(string guid)
    {
        var probed = Probed(partitionTypeGuid: guid, mountPoints: []);

        VolumeClassifier.Classify(probed).Should().Be(VolumeKind.System);
    }

    [Theory]
    [InlineData("SYSTEM_DRV")]
    [InlineData("WINRE_DRV")]
    [InlineData("Recovery")]
    [InlineData("EFI")]
    public void Letterless_with_system_label_is_system_fallback(string label)
    {
        // No partition GUID available → fall back on no-letter + known system label.
        var probed = Probed(partitionTypeGuid: null, label: label, mountPoints: []);

        VolumeClassifier.Classify(probed).Should().Be(VolumeKind.System);
    }

    [Fact]
    public void Letterless_small_fat32_is_system_fallback()
    {
        // ESP-like: tiny FAT32 partition with no drive letter and no GUID/label.
        var probed = Probed(
            partitionTypeGuid: null,
            label: null,
            mountPoints: [],
            fileSystem: "FAT32",
            capacityBytes: 100L * 1024 * 1024);

        VolumeClassifier.Classify(probed).Should().Be(VolumeKind.System);
    }

    [Fact]
    public void Removable_drive_is_removable()
    {
        VolumeClassifier.Classify(Probed(driveType: DriveType.Removable)).Should().Be(VolumeKind.Removable);
    }

    [Fact]
    public void Fixed_drive_with_letter_is_fixed()
    {
        VolumeClassifier.Classify(Probed(driveType: DriveType.Fixed)).Should().Be(VolumeKind.Fixed);
    }

    [Fact]
    public void Unrecognised_signals_are_unknown()
    {
        VolumeClassifier.Classify(Probed(driveType: DriveType.Unknown)).Should().Be(VolumeKind.Unknown);
    }

    [Theory]
    [InlineData(VolumeKind.Fixed, true)]
    [InlineData(VolumeKind.Removable, true)]
    [InlineData(VolumeKind.Unknown, true)]
    [InlineData(VolumeKind.Cloud, false)]
    [InlineData(VolumeKind.System, false)]
    public void Default_catalogable_follows_kind(VolumeKind kind, bool expected)
    {
        VolumeClassifier.DefaultCatalogable(kind).Should().Be(expected);
    }
}
