using System.Management;
using FileTracert.Platform.Internal;
using Microsoft.Extensions.Logging;

namespace FileTracert.Platform;

/// <summary>
/// Resolves the descriptive physical-disk id for a drive letter via WMI
/// (<c>Win32_DiskDrive → Win32_DiskPartition → Win32_LogicalDisk</c>).
/// Best-effort only: any failure yields an empty map and is never fatal to
/// volume enumeration (CLAUDE.md §4 — topology is metadata, not the key).
/// </summary>
internal sealed class WmiPhysicalDiskResolver : IPhysicalDiskResolver
{
    private readonly ILogger<WmiPhysicalDiskResolver> _logger;

    public WmiPhysicalDiskResolver(ILogger<WmiPhysicalDiskResolver> logger)
    {
        _logger = logger;
    }

    public IReadOnlyDictionary<string, string> BuildDriveLetterToDiskMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_DiskDrive");
            using var disks = searcher.Get();

            foreach (ManagementBaseObject diskBase in disks)
            {
                using var disk = (ManagementObject)diskBase;
                var diskId = disk["DeviceID"]?.ToString();
                if (string.IsNullOrEmpty(diskId))
                {
                    continue;
                }

                MapLogicalDisksForDisk(disk, diskId, map);
            }
        }
        catch (ManagementException ex)
        {
            _logger.LogDebug(ex, "WMI disk topology unavailable; PhysicalDiskId will be null.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "WMI access denied; PhysicalDiskId will be null.");
        }

        return map;
    }

    private static void MapLogicalDisksForDisk(
        ManagementObject disk,
        string diskId,
        IDictionary<string, string> map)
    {
        using var partitions = disk.GetRelated("Win32_DiskPartition");
        foreach (ManagementBaseObject partitionBase in partitions)
        {
            using var partition = (ManagementObject)partitionBase;
            using var logicalDisks = partition.GetRelated("Win32_LogicalDisk");
            foreach (ManagementBaseObject logicalBase in logicalDisks)
            {
                using var logical = (ManagementObject)logicalBase;
                var key = VolumePathParsing.DriveLetterKey(logical["DeviceID"]?.ToString());
                if (key is not null)
                {
                    map[key] = diskId;
                }
            }
        }
    }
}
