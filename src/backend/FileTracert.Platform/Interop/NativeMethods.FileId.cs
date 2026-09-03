using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileTracert.Platform.Interop;

/// <summary>
/// Reading the NTFS <em>file reference number</em> — the identity the change journal speaks in —
/// for objects reached by path rather than by walking the MFT.
/// </summary>
/// <remarks>
/// Two calls, because there are two questions. <c>GetFileInformationByHandleEx</c> with
/// <c>FileIdBothDirectoryInfo</c> answers "what are the children of this directory, and what are
/// their ids", one buffer at a time and without opening a handle per child; the id arrives
/// alongside the name, which is the only way to pair them without a second lookup that could race
/// a rename. <c>GetFileInformationByHandle</c> answers the same question about ONE object already
/// open, which is what a watched root needs: the walk starts inside it and never yields it, yet
/// the delta resolves records whose parent is exactly it.
/// </remarks>
internal static partial class NativeMethods
{
    /// <summary><c>FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryInfo</c>.</summary>
    internal const int FileIdBothDirectoryInfo = 0xa;

    /// <summary><c>FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryRestartInfo</c>.</summary>
    internal const int FileIdBothDirectoryRestartInfo = 0xb;

    /// <summary>Enough to read attributes and identity; no data access, so nothing is locked.</summary>
    internal const uint FileReadAttributes = 0x0080;
    internal const uint FileListDirectory = 0x0001;
    internal const uint FileShareDelete = 0x00000004;

    /// <summary>Returned by the directory-info call when the previous buffer was the last one.</summary>
    internal const int ErrorNoMoreFiles = 18;

    // --- FILE_ID_BOTH_DIR_INFO, the offsets this code actually reads ------------------------
    //
    // The struct is variable-length (the name is a trailing array), so it is walked by offset
    // rather than marshalled: NextEntryOffset chains the entries inside one buffer, and a zero
    // there means "last one".
    //
    // The two offsets that are easy to get wrong are FileId and FileName, and the arithmetic is
    // worth writing down: ShortNameLength is a single byte at 68, padded to 70 where the 12-WCHAR
    // ShortName starts, so the short name ends at 94 — and FileId, being 8-aligned, starts at 96,
    // not at 94. Getting this wrong is not a crash: the ids come back plausible and the names come
    // back as fragments, which is exactly what the first run of these tests showed.
    internal const int FileIdBothDirInfoNextEntryOffset = 0;
    internal const int FileIdBothDirInfoFileNameLength = 60;
    internal const int FileIdBothDirInfoFileId = 96;
    internal const int FileIdBothDirInfoFileName = 104;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        void* lpFileInformation,
        uint dwBufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);
}

/// <summary>
/// Output of <c>GetFileInformationByHandle</c>. Only the two index halves are read: together they
/// are the 64-bit file reference number, the same value <c>FSCTL_ENUM_USN_DATA</c> reports.
/// </summary>
/// <remarks>
/// Every field is four bytes, and the three timestamps are spelled as their <c>FILETIME</c> halves
/// rather than as <c>long</c>, because that is what the native struct is: a <c>FILETIME</c> is two
/// DWORDs and needs no eight-byte alignment. Declaring them as <c>long</c> makes the CLR insert
/// four bytes of padding after <c>FileAttributes</c> and every field after it reads the wrong
/// memory — silently, since the values that come back are still plausible numbers. That is not a
/// hypothetical: the first version of this file did exactly that, and the two ways of asking for
/// the same directory's identity disagreed.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct ByHandleFileInformation
{
    public uint FileAttributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}
