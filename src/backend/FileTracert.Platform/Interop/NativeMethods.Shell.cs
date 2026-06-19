using System.Runtime.InteropServices;

namespace FileTracert.Platform.Interop;

// Shell32 declarations live here, separate from kernel32 (NativeMethods.cs).
// SHFileOperation uses DllImport (not LibraryImport) because the struct contains
// a string field that needs manual double-null termination — source generation
// cannot handle this marshalling pattern safely.
internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        /// <summary>Source path(s), double-null terminated. Set pFrom = path + '\0'.</summary>
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    internal const uint FO_DELETE = 3;
    internal const ushort FOF_SILENT = 0x0004;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_NOERRORUI = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
