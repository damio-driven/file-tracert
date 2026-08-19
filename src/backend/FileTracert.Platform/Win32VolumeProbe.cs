using System.IO;
using System.Runtime.InteropServices;
using FileTracert.Contracts.Platform;
using FileTracert.Platform.Internal;
using FileTracert.Platform.Interop;
using Microsoft.Extensions.Logging;

namespace FileTracert.Platform;

/// <summary>
/// Win32 implementation of <see cref="IVolumeProbe"/>. Uses the volume APIs for
/// identity/metadata (they also see partitions without a letter) and confines
/// WMI to physical-disk topology only.
/// </summary>
internal sealed class Win32VolumeProbe : IVolumeProbe
{
    private readonly IPhysicalDiskResolver _diskResolver;
    private readonly ILogger<Win32VolumeProbe> _logger;

    public Win32VolumeProbe(IPhysicalDiskResolver diskResolver, ILogger<Win32VolumeProbe> logger)
    {
        _diskResolver = diskResolver;
        _logger = logger;
    }

    public IReadOnlyList<ProbedVolume> EnumerateVolumes()
    {
        var results = new List<ProbedVolume>();
        var diskMap = _diskResolver.BuildDriveLetterToDiskMap();

        var buffer = new char[NativeMethods.MaxPath];
        var handle = NativeMethods.FindFirstVolume(buffer, (uint)buffer.Length);
        if (handle == NativeMethods.InvalidHandleValue)
        {
            _logger.LogWarning(
                "FindFirstVolume failed (Win32 error {Error}); returning no volumes.",
                Marshal.GetLastWin32Error());
            return results;
        }

        try
        {
            do
            {
                var guid = VolumePathParsing.ReadNullTerminated(buffer);
                // A removable volume can vanish mid-enumeration: never let one
                // bad volume abort the whole sweep.
                try
                {
                    results.Add(BuildVolume(guid, diskMap));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping volume {Guid} that failed to probe.", guid);
                }
            }
            while (NativeMethods.FindNextVolume(handle, buffer, (uint)buffer.Length));
        }
        finally
        {
            NativeMethods.FindVolumeClose(handle);
        }

        return results;
    }

    public ProbedVolume? TryGetByGuid(string volumeGuid)
    {
        if (!VolumePathParsing.IsVolumeGuidPath(volumeGuid))
        {
            return null;
        }

        return EnumerateVolumes()
            .FirstOrDefault(v => string.Equals(v.VolumeGuid, volumeGuid, StringComparison.OrdinalIgnoreCase));
    }

    public long? TryGetFreeBytes(string volumeGuid)
    {
        if (!VolumePathParsing.IsVolumeGuidPath(volumeGuid))
        {
            _logger.LogWarning("Free-space probe refused a value that is not a volume GUID path: '{Guid}'.", volumeGuid);
            return null;
        }

        // One syscall against the volume itself. It doubles as a presence check: an unmounted
        // or removed volume fails here instead of returning a stale number.
        //
        // The figure returned is lpFreeBytesAvailableToCaller, NOT lpTotalNumberOfFreeBytes: the
        // caller of this method is about to commit a copy to it, and under an NTFS disk quota the
        // volume-wide free space is room this process may not write to. The two differ only when
        // a quota is in force, and exactly there the larger number would pass the check and let
        // the copy die out of space. Math.Min because a quota can also be reported the other way
        // round on some filesystems: the smaller of the two is the one that can actually be used.
        if (NativeMethods.GetDiskFreeSpaceEx(volumeGuid, out var availableToCaller, out _, out var totalFree))
        {
            return (long)Math.Min(availableToCaller, totalFree);
        }

        // Not silent (§9): the caller turns this into a Blocked job, and the reason why the
        // device could not answer belongs in the log.
        _logger.LogWarning(
            "GetDiskFreeSpaceEx failed for volume {Guid} (Win32 error {Error}); the volume is not readable right now.",
            volumeGuid, Marshal.GetLastWin32Error());
        return null;
    }

    private ProbedVolume BuildVolume(string volumeGuid, IReadOnlyDictionary<string, string> diskMap)
    {
        var mountPoints = GetMountPoints(volumeGuid);
        var (label, serial, fileSystem) = GetVolumeInformation(volumeGuid);
        // GetDriveType values map 1:1 onto System.IO.DriveType.
        var driveType = (DriveType)NativeMethods.GetDriveType(volumeGuid);
        var isRemovable = driveType == DriveType.Removable;
        var (capacity, free) = GetSpace(volumeGuid);
        var physicalDiskId = ResolvePhysicalDiskId(mountPoints, diskMap);
        var disk = VolumeDiskInfo.Read(volumeGuid);

        return new ProbedVolume(
            volumeGuid,
            serial,
            label,
            fileSystem,
            isRemovable,
            mountPoints,
            capacity,
            free,
            physicalDiskId,
            driveType,
            disk.HasPhysicalExtents,
            disk.PartitionTypeGuid);
    }

    private static IReadOnlyList<string> GetMountPoints(string volumeGuid)
    {
        var buffer = new char[NativeMethods.MaxPath + 2];
        if (!NativeMethods.GetVolumePathNamesForVolumeName(
                volumeGuid, buffer, (uint)buffer.Length, out var returnLength))
        {
            if (Marshal.GetLastWin32Error() != NativeMethods.ErrorMoreData)
            {
                return [];
            }

            buffer = new char[returnLength];
            if (!NativeMethods.GetVolumePathNamesForVolumeName(
                    volumeGuid, buffer, (uint)buffer.Length, out returnLength))
            {
                return [];
            }
        }

        var used = Math.Min((int)returnLength, buffer.Length);
        return VolumePathParsing.SplitDoubleNullTerminated(buffer.AsSpan(0, used));
    }

    private static (string? Label, string? Serial, string FileSystem) GetVolumeInformation(string volumeGuid)
    {
        var labelBuffer = new char[NativeMethods.MaxPath + 1];
        var fileSystemBuffer = new char[NativeMethods.MaxPath + 1];

        if (!NativeMethods.GetVolumeInformation(
                volumeGuid,
                labelBuffer,
                (uint)labelBuffer.Length,
                out var serialNumber,
                out _,
                out _,
                fileSystemBuffer,
                (uint)fileSystemBuffer.Length))
        {
            // Not ready (e.g. empty card reader): leave metadata blank.
            return (null, null, string.Empty);
        }

        var label = VolumePathParsing.ReadNullTerminated(labelBuffer);
        return (
            label.Length > 0 ? label : null,
            VolumePathParsing.FormatSerial(serialNumber),
            VolumePathParsing.ReadNullTerminated(fileSystemBuffer));
    }

    private static (long Capacity, long Free) GetSpace(string volumeGuid)
    {
        if (NativeMethods.GetDiskFreeSpaceEx(volumeGuid, out _, out var total, out var totalFree))
        {
            return ((long)total, (long)totalFree);
        }

        return (0, 0);
    }

    private static string? ResolvePhysicalDiskId(
        IReadOnlyList<string> mountPoints,
        IReadOnlyDictionary<string, string> diskMap)
    {
        foreach (var mountPoint in mountPoints)
        {
            var key = VolumePathParsing.DriveLetterKey(mountPoint);
            if (key is not null && diskMap.TryGetValue(key, out var diskId))
            {
                return diskId;
            }
        }

        return null;
    }
}
