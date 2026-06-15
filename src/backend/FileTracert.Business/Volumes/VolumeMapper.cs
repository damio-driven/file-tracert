using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Volumes;

/// <summary>
/// Pure mapping between a live <see cref="ProbedVolume"/> and the persisted
/// <see cref="Volume"/> entity. Kept free of DB/clock access so it is trivially
/// testable; callers pass the timestamp.
/// </summary>
public static class VolumeMapper
{
    /// <summary>NTFS gets the USN engine; everything else falls back to enumeration.</summary>
    public static VolumeScanEngine EngineFor(string fileSystem) =>
        string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
            ? VolumeScanEngine.UsnJournal
            : VolumeScanEngine.Enumeration;

    /// <summary>Drive-letter form (<c>C:</c>) of the first mount point, or null.</summary>
    public static string? DriveLetterOf(ProbedVolume probed)
    {
        var mount = probed.MountPoints.Count > 0 ? probed.MountPoints[0] : null;
        if (mount is null || mount.Length < 2 || mount[1] != ':')
        {
            return null;
        }

        return $"{char.ToUpperInvariant(mount[0])}:";
    }

    /// <summary>Creates a brand-new <see cref="Volume"/> from a probe result.</summary>
    public static Volume MapNew(ProbedVolume probed, DateTime nowUtc)
    {
        var kind = VolumeClassifier.Classify(probed);
        return new Volume
        {
            VolumeGuid = probed.VolumeGuid,
            SerialNumber = probed.SerialNumber,
            Label = probed.Label,
            FileSystem = probed.FileSystem,
            IsRemovable = probed.IsRemovable,
            Kind = kind,
            IsCatalogable = VolumeClassifier.DefaultCatalogable(kind),
            PhysicalDiskId = probed.PhysicalDiskId,
            LastDriveLetter = DriveLetterOf(probed),
            CapacityBytes = probed.CapacityBytes,
            FreeBytesLastKnown = probed.FreeBytes,
            ScanEngine = EngineFor(probed.FileSystem),
            IsOnline = true,
            LastSeenUtc = nowUtc,
        };
    }

    /// <summary>
    /// Refreshes only the live state of an existing volume. Deliberately leaves
    /// scan checkpoints (<see cref="Volume.LastUsn"/>, <see cref="Volume.LastFullScanUtc"/>)
    /// and the chosen <see cref="Volume.ScanEngine"/> untouched. The classification
    /// (<see cref="Volume.Kind"/>) is recomputed, but a manual <see cref="Volume.IsCatalogable"/>
    /// override is preserved: it is only re-derived when it still matches the default of the
    /// previously stored kind (i.e. the user never changed it).
    /// </summary>
    public static void ApplyLiveState(Volume volume, ProbedVolume probed, DateTime nowUtc)
    {
        volume.SerialNumber = probed.SerialNumber;
        volume.Label = probed.Label;
        volume.FileSystem = probed.FileSystem;
        volume.IsRemovable = probed.IsRemovable;
        volume.PhysicalDiskId = probed.PhysicalDiskId;
        volume.LastDriveLetter = DriveLetterOf(probed);
        volume.CapacityBytes = probed.CapacityBytes;
        volume.FreeBytesLastKnown = probed.FreeBytes;
        volume.IsOnline = true;
        volume.LastSeenUtc = nowUtc;

        var newKind = VolumeClassifier.Classify(probed);
        if (volume.IsCatalogable == VolumeClassifier.DefaultCatalogable(volume.Kind))
        {
            volume.IsCatalogable = VolumeClassifier.DefaultCatalogable(newKind);
        }

        volume.Kind = newKind;
    }
}
