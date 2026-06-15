using FileTracert.Platform.Interop;
using Microsoft.Win32.SafeHandles;

namespace FileTracert.Platform.Internal;

/// <summary>
/// Best-effort classification signals read straight off a volume handle:
/// whether it maps onto physical disk extents, and its GPT partition type GUID.
/// Every failure is swallowed into a safe default — a cloud/virtual mount has no
/// extents, a real disk does, and the partition GUID is simply absent when it
/// cannot be read. Never throws (CLAUDE.md §B1: must not break enumeration).
/// </summary>
internal static class VolumeDiskInfo
{
    /// <summary>Offset of the GPT partition type GUID inside PARTITION_INFORMATION_EX.</summary>
    private const int GptPartitionTypeOffset = 32;

    /// <summary>PARTITION_STYLE.GPT.</summary>
    private const uint PartitionStyleGpt = 1;

    internal readonly record struct Signals(bool HasPhysicalExtents, string? PartitionTypeGuid);

    /// <summary>
    /// Reads the disk signals for a volume GUID path. When the handle cannot be
    /// opened the result is the safe default (treated as a real disk, no GUID) so
    /// a transient failure never mislabels a genuine volume as cloud.
    /// </summary>
    public static Signals Read(string volumeGuid)
    {
        // CreateFile wants the GUID path without its trailing backslash.
        var devicePath = volumeGuid.TrimEnd('\\');

        // Zero desired access is enough for these read-only IOCTLs (no elevation needed),
        // and it works on volumes we couldn't otherwise open for data.
        using SafeFileHandle handle = NativeMethods.CreateFile(
            devicePath,
            dwDesiredAccess: 0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            dwFlagsAndAttributes: 0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return new Signals(HasPhysicalExtents: true, PartitionTypeGuid: null);
        }

        return new Signals(QueryHasExtents(handle), QueryPartitionTypeGuid(handle));
    }

    /// <summary>
    /// True only when the volume positively reports ≥1 disk extent. A virtual/cloud
    /// volume answers the IOCTL with failure or zero extents → false.
    /// </summary>
    private static unsafe bool QueryHasExtents(SafeFileHandle handle)
    {
        // VOLUME_DISK_EXTENTS: DWORD NumberOfDiskExtents; DWORD pad; DISK_EXTENT[] (24 bytes each).
        // Room for several extents so a spanned volume still succeeds in one call.
        var buffer = new byte[8 + (24 * 16)];
        fixed (byte* p = buffer)
        {
            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.IoctlVolumeGetVolumeDiskExtents,
                    null, 0,
                    p, (uint)buffer.Length,
                    out _, IntPtr.Zero))
            {
                return false;
            }
        }

        var numberOfExtents = BitConverter.ToUInt32(buffer, 0);
        return numberOfExtents > 0;
    }

    /// <summary>GPT partition type GUID (lower-case, no braces), or null for MBR/RAW/failure.</summary>
    private static unsafe string? QueryPartitionTypeGuid(SafeFileHandle handle)
    {
        var buffer = new byte[144]; // sizeof(PARTITION_INFORMATION_EX)
        uint returned;
        fixed (byte* p = buffer)
        {
            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.IoctlDiskGetPartitionInfoEx,
                    null, 0,
                    p, (uint)buffer.Length,
                    out returned, IntPtr.Zero))
            {
                return null;
            }
        }

        if (returned < GptPartitionTypeOffset + 16)
        {
            return null;
        }

        var style = BitConverter.ToUInt32(buffer, 0);
        if (style != PartitionStyleGpt)
        {
            return null;
        }

        var guid = new Guid(buffer.AsSpan(GptPartitionTypeOffset, 16));
        return guid.ToString();
    }
}
