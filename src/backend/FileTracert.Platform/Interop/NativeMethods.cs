using System.Runtime.InteropServices;
using System.Text;

namespace FileTracert.Platform.Interop;

/// <summary>
/// Raw Win32 P/Invoke declarations. Internal to Platform — no native call ever
/// escapes this assembly (see CLAUDE.md §3).
/// </summary>
internal static class NativeMethods
{
    internal const int MaxPath = 260;
    internal const int ErrorMoreData = 234;

    /// <summary><c>GetDriveType</c> result for removable media.</summary>
    internal const uint DriveRemovable = 2;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindFirstVolume(
        [Out] StringBuilder lpszVolumeName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextVolume(
        IntPtr hFindVolume,
        [Out] StringBuilder lpszVolumeName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindVolumeClose(IntPtr hFindVolume);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumePathNamesForVolumeName(
        string lpszVolumeName,
        [Out] char[]? lpszVolumePathNames,
        uint cchBufferLength,
        out uint lpcchReturnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformation(
        string lpRootPathName,
        [Out] StringBuilder lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        [Out] StringBuilder lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetDriveType(string lpRootPathName);
}
