using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileTracert.Platform.Interop;

/// <summary>
/// USN/journal-specific interop: opening the volume handle and issuing the
/// <c>FSCTL_*</c> device-control codes.
/// </summary>
internal static partial class NativeMethods
{
    // --- CreateFile access / share / disposition / flags ---
    internal const uint GenericRead = 0x80000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;

    // --- FSCTL control codes (FILE_DEVICE_FILE_SYSTEM = 9) ---
    internal const uint FsctlQueryUsnJournal = 0x000900F4;
    internal const uint FsctlCreateUsnJournal = 0x000900E7;
    internal const uint FsctlEnumUsnData = 0x000900B3;
    internal const uint FsctlReadUsnJournal = 0x000900BB;

    // --- Win32 errors relevant to the journal ---
    internal const int ErrorHandleEof = 38;
    internal const int ErrorJournalDeleteInProgress = 1178;
    internal const int ErrorJournalNotActive = 1179;
    internal const int ErrorJournalEntryDeleted = 1181;
    internal const int ErrorInvalidFunction = 1;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        uint nInBufferSize,
        void* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
