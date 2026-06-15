namespace FileTracert.Platform.Interop;

/// <summary>
/// Disk/volume IOCTL control codes used to derive classification signals
/// (physical extents, GPT partition type). The <c>CreateFile</c>/<c>DeviceIoControl</c>
/// imports themselves live alongside the USN interop. All best-effort — callers
/// must tolerate failure and never let it abort enumeration (CLAUDE.md §B1).
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS — does this volume sit on real disk extents?</summary>
    internal const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    /// <summary>IOCTL_DISK_GET_PARTITION_INFO_EX — partition style + GPT partition type GUID.</summary>
    internal const uint IoctlDiskGetPartitionInfoEx = 0x00070048;
}
