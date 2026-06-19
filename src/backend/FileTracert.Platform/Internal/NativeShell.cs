using FileTracert.Platform.Interop;
using Microsoft.Extensions.Logging;

namespace FileTracert.Platform.Internal;

/// <summary>
/// Headless Recycle-Bin wrapper using SHFileOperation. Runs without UI:
/// FOF_SILENT suppresses all dialogs, FOF_ALLOWUNDO routes to the Recycle Bin.
/// </summary>
internal static class NativeShell
{
    internal static void SendToRecycleBin(string fullPath, ILogger? logger = null)
    {
        // pFrom must be double-null terminated. The [CharSet = Unicode] marshaler adds
        // one trailing \0; we add another manually so the list ends with \0\0.
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc = NativeMethods.FO_DELETE,
            pFrom = fullPath + '\0',
            fFlags = NativeMethods.FOF_ALLOWUNDO
                   | NativeMethods.FOF_NOCONFIRMATION
                   | NativeMethods.FOF_SILENT
                   | NativeMethods.FOF_NOERRORUI,
        };

        var code = NativeMethods.SHFileOperation(ref op);
        if (code != 0)
        {
            logger?.LogWarning(
                "SHFileOperation(FO_DELETE) returned {Code} for {Path}; " +
                "file may not have been sent to Recycle Bin.",
                code, fullPath);
            throw new InvalidOperationException(
                $"SHFileOperation failed with code {code} for: {fullPath}");
        }
    }
}
