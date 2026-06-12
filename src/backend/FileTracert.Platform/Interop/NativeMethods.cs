using System.Runtime.InteropServices;

namespace FileTracert.Platform.Interop;

/// <summary>
/// Raw Win32 P/Invoke declarations (source-generated via LibraryImport).
/// Internal to Platform — no native call ever escapes this assembly
/// (see CLAUDE.md §3).
/// </summary>
internal static partial class NativeMethods
{
    internal const int MaxPath = 260;
    internal const int ErrorMoreData = 234;

    /// <summary><c>GetDriveType</c> result for removable media.</summary>
    internal const uint DriveRemovable = 2;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindFirstVolume(
        [Out] char[] lpszVolumeName,
        uint cchBufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextVolume(
        IntPtr hFindVolume,
        [Out] char[] lpszVolumeName,
        uint cchBufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindVolumeClose(IntPtr hFindVolume);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathNamesForVolumeName(
        string lpszVolumeName,
        [Out] char[] lpszVolumePathNames,
        uint cchBufferLength,
        out uint lpcchReturnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformation(
        string lpRootPathName,
        [Out] char[] lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        [Out] char[] lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveType(string lpRootPathName);
}
