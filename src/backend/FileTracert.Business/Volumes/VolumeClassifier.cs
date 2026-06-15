using System.IO;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;

namespace FileTracert.Business.Volumes;

/// <summary>
/// Pure classifier mapping a live <see cref="ProbedVolume"/> onto a
/// <see cref="VolumeKind"/>. No interop, no DB, no clock — just signals in, kind
/// out, so it is trivially unit-testable. Rules are conservative and ordered
/// (CLAUDE.md §B2): a cloud mount is caught before a system partition, both
/// before the plain fixed/removable distinction.
/// </summary>
public static class VolumeClassifier
{
    // GPT partition type GUIDs that mark a partition as "system" (not for cataloguing).
    private static readonly HashSet<string> SystemPartitionGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", // EFI System Partition (ESP)
        "e3c9e316-0b5c-4db8-817d-f92df00215ae", // Microsoft Reserved (MSR)
        "de94bba4-06d1-4d40-a16a-bfd50179d6ac", // Windows Recovery (WinRE)
    };

    // Labels Windows gives its hidden system/recovery partitions.
    private static readonly HashSet<string> SystemLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYSTEM_DRV", "WINRE_DRV", "Recovery", "EFI",
    };

    // An ESP-like partition is small (Windows ships ~100–300 MB) and FAT32.
    private const long EspMaxBytes = 1L * 1024 * 1024 * 1024;

    public static VolumeKind Classify(ProbedVolume probed)
    {
        // 1a. The IOCTL confirmed zero extents → virtual/cloud mount.
        if (probed.HasPhysicalExtents == false)
        {
            return VolumeKind.Cloud;
        }

        // 1b. The volume device handle could not be opened at all (HasPhysicalExtents == null)
        //     AND the volume is absent from the WMI physical-disk topology → cloud / network FS.
        //     Guard: if PhysicalDiskId is set the disk was seen by WMI; don't hide it on a
        //     transient IOCTL failure.
        if (probed.HasPhysicalExtents == null && probed.PhysicalDiskId == null)
        {
            return VolumeKind.Cloud;
        }

        // 2. Explicit GPT system partition type.
        if (probed.PartitionTypeGuid is { } guid && SystemPartitionGuids.Contains(NormalizeGuid(guid)))
        {
            return VolumeKind.System;
        }

        var hasMountPoint = probed.MountPoints.Count > 0;

        // 3. Fallback when the GUID isn't available: a partition with no drive letter that
        //    either carries a known system label or looks like a tiny FAT32 ESP.
        if (!hasMountPoint && IsSystemFallback(probed))
        {
            return VolumeKind.System;
        }

        // 4. Plain media distinction from the drive type.
        return probed.DriveType switch
        {
            DriveType.Removable => VolumeKind.Removable,
            DriveType.Fixed => VolumeKind.Fixed,
            _ => VolumeKind.Unknown,
        };
    }

    /// <summary>Default catalogable flag derived from the kind: hide cloud/system, keep the rest.</summary>
    public static bool DefaultCatalogable(VolumeKind kind) => kind switch
    {
        VolumeKind.Cloud => false,
        VolumeKind.System => false,
        _ => true,
    };

    private static bool IsSystemFallback(ProbedVolume probed)
    {
        if (probed.Label is { Length: > 0 } label && SystemLabels.Contains(label))
        {
            return true;
        }

        return string.Equals(probed.FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase)
            && probed.CapacityBytes > 0
            && probed.CapacityBytes <= EspMaxBytes;
    }

    private static string NormalizeGuid(string guid) =>
        guid.Trim().Trim('{', '}');
}
