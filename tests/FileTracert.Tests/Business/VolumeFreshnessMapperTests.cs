using FileTracert.Business.Volumes;
using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Business;

public sealed class VolumeFreshnessMapperTests
{
    private static Volume Sample(bool online) => new()
    {
        Id = 7,
        VolumeGuid = @"\\?\Volume{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}\",
        Label = "Archivio",
        LastDriveLetter = "H:",
        FileSystem = "NTFS",
        IsRemovable = true,
        IsOnline = online,
        LastSeenUtc = new DateTime(2026, 6, 10, 9, 14, 0, DateTimeKind.Utc),
        CapacityBytes = 1_000,
        FreeBytesLastKnown = 128,
        ScanEngine = VolumeScanEngine.UsnJournal,
        LastUsn = 44_821_330,
        SerialNumber = "B83A-77F1",
        PhysicalDiskId = @"\\.\PHYSICALDRIVE2",
        Kind = VolumeKind.Removable,
        IsCatalogable = true,
    };

    [Fact]
    public void Online_volume_is_live_not_stale()
    {
        var dto = VolumeFreshnessMapper.ToDto(Sample(online: true), fileCount: 48_054);

        dto.IsOnline.Should().BeTrue();
        dto.DataIsLive.Should().BeTrue();
        dto.IsStale.Should().BeFalse();
        dto.FreeBytes.Should().Be(128);
        dto.FileCount.Should().Be(48_054);
        dto.CurrentLetter.Should().Be("H:");
        dto.Kind.Should().Be("Removable");
        dto.IsCatalogable.Should().BeTrue();
    }

    [Fact]
    public void Offline_volume_is_stale_with_last_known_free()
    {
        var dto = VolumeFreshnessMapper.ToDto(Sample(online: false), fileCount: 10);

        dto.DataIsLive.Should().BeFalse();
        dto.IsStale.Should().BeTrue();
        // Free is the last-known snapshot, surfaced as-is for the UI to flag.
        dto.FreeBytes.Should().Be(128);
    }

    [Fact]
    public void Detail_carries_identity_roots_and_index_stats()
    {
        var roots = new[] { new FileTracert.Contracts.Dtos.WatchedRootDto(1, @"\Foto", true, "Immagini") };

        var dto = VolumeFreshnessMapper.ToDetailDto(Sample(online: false), roots, directoryCount: 12, fileCount: 48_054, indexedBytes: 119_000);

        dto.SerialNumber.Should().Be("B83A-77F1");
        dto.ScanEngine.Should().Be("UsnJournal");
        dto.LastUsn.Should().Be(44_821_330);
        dto.WatchedRoots.Should().ContainSingle();
        dto.DirectoryCount.Should().Be(12);
        dto.IndexedBytes.Should().Be(119_000);
        dto.IsStale.Should().BeTrue();
    }
}
