using System.ComponentModel;
using System.Runtime.InteropServices;
using FileTracert.Platform.Interop;
using Microsoft.Win32.SafeHandles;

namespace FileTracert.Platform.Internal;

/// <summary>
/// Reads NTFS file reference numbers for a directory's children in one pass, and for a single
/// object by path.
/// </summary>
/// <remarks>
/// <para>One directory handle answers for every child, so the cost is per directory and not per
/// entry — the point of using the directory-info class instead of opening each child. The names
/// come back in the same records as the ids, so the pairing cannot be raced by a rename happening
/// between two calls.</para>
/// <para>Failure is answered with an empty map (or a null id), never an exception: a folder we
/// cannot open is one the enumerator already skips, and a volume whose filesystem has no such
/// identity (FAT) simply reports zeros. Deciding whether a zero means anything is the caller's
/// business, not this file's — here a zero is reported as "no id".</para>
/// </remarks>
internal static class DirectoryFileIds
{
    /// <summary>Big enough that a normal directory needs one or two round trips.</summary>
    private const int BufferBytes = 64 * 1024;

    /// <summary>
    /// Maps child name → file reference number for <paramref name="directoryPath"/>.
    /// Names are compared case-insensitively, as the filesystem does.
    /// </summary>
    public static Dictionary<string, ulong> ForChildren(string directoryPath)
    {
        var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        using var handle = OpenForQuery(directoryPath, directory: true);
        if (handle is null)
        {
            return map;
        }

        Read(handle, map);
        return map;
    }

    /// <summary>
    /// The file reference number of one object named by its full path, or null when it cannot be
    /// opened or the filesystem does not give it one.
    /// </summary>
    public static ulong? ForPath(string path)
    {
        // Attributes only: asking for list/read access would fail on a file someone else holds
        // exclusively, and identity is all this needs.
        using var handle = OpenForQuery(path, directory: false);
        if (handle is null)
        {
            return null;
        }

        if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        var id = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return id == 0 ? null : id;
    }

    private static unsafe void Read(SafeFileHandle handle, Dictionary<string, ulong> map)
    {
        var buffer = Marshal.AllocHGlobal(BufferBytes);
        try
        {
            var infoClass = NativeMethods.FileIdBothDirectoryRestartInfo;

            while (NativeMethods.GetFileInformationByHandleEx(
                       handle, infoClass, buffer.ToPointer(), BufferBytes))
            {
                // Only the first call restarts the scan; every later one continues it.
                infoClass = NativeMethods.FileIdBothDirectoryInfo;

                var entry = (byte*)buffer.ToPointer();
                while (true)
                {
                    var next = *(uint*)(entry + NativeMethods.FileIdBothDirInfoNextEntryOffset);
                    var nameBytes = *(uint*)(entry + NativeMethods.FileIdBothDirInfoFileNameLength);
                    var fileId = *(ulong*)(entry + NativeMethods.FileIdBothDirInfoFileId);

                    var name = new string(
                        (char*)(entry + NativeMethods.FileIdBothDirInfoFileName),
                        0,
                        (int)(nameBytes / sizeof(char)));

                    // "." and ".." are entries too, and neither is a child.
                    if (fileId != 0 && name is not ("." or ".."))
                    {
                        map[name] = fileId;
                    }

                    if (next == 0)
                    {
                        break;
                    }

                    entry += next;
                }
            }

            // Running out of entries is how the loop is meant to end; anything else is a real
            // failure, and the caller keeps whatever was read rather than losing the directory.
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorNoMoreFiles)
            {
                throw new Win32Exception(error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Opens for metadata only. <c>FILE_FLAG_BACKUP_SEMANTICS</c> is what makes a directory
    /// openable at all; the share mode includes delete so enumerating never blocks anyone.
    /// </summary>
    private static SafeFileHandle? OpenForQuery(string path, bool directory)
    {
        var handle = NativeMethods.CreateFile(
            path,
            NativeMethods.FileReadAttributes | (directory ? NativeMethods.FileListDirectory : 0),
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }
}
