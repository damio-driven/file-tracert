using System.ComponentModel;
using FileTracert.Contracts.Platform;
using FileTracert.Platform.Internal;
using Microsoft.Extensions.Logging;

namespace FileTracert.Platform;

/// <summary>
/// BCL directory-enumeration fallback for volumes without a USN journal
/// (exFAT/FAT32). Walks the tree iteratively, skipping folders we cannot read
/// without aborting the whole sweep. Size and timestamps come straight from the
/// filesystem.
/// </summary>
internal sealed class ManagedDirectoryEnumerator : IDirectoryEnumerator
{
    private readonly ILogger<ManagedDirectoryEnumerator> _logger;

    public ManagedDirectoryEnumerator(ILogger<ManagedDirectoryEnumerator> logger)
    {
        _logger = logger;
    }

    public IEnumerable<ScanEntry> Enumerate(string mountRoot, string relativeRoot, CancellationToken ct)
    {
        var start = string.IsNullOrEmpty(relativeRoot)
            ? mountRoot
            : Path.Combine(mountRoot, relativeRoot);

        var stack = new Stack<string>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = stack.Pop();

            // One handle answers for every child of this directory, so identity costs per
            // directory rather than per entry. Read BEFORE the children are listed, so a name
            // that disappears in between simply has no id instead of borrowing someone else's.
            var fileIds = SafeFileIds(directory);

            foreach (var path in SafeEnumerate(directory))
            {
                var entry = TryBuildEntry(path, mountRoot, fileIds);
                if (entry is null)
                {
                    continue;
                }

                // Never walk THROUGH a reparse point (junction/symlink): the entry itself is
                // yielded, but descending would duplicate content that lives elsewhere and a
                // junction into an ancestor (e.g. AppData\Local\Application Data) loops forever.
                if (entry.IsDirectory && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    stack.Push(path);
                }

                yield return entry;
            }
        }
    }

    /// <summary>Lists one directory's children; a denied/missing folder yields nothing.</summary>
    private string[] SafeEnumerate(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Access denied, skipping {Directory}.", directory);
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogDebug(ex, "Directory vanished mid-scan, skipping {Directory}.", directory);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "I/O error enumerating {Directory}; skipping.", directory);
        }

        return [];
    }

    /// <summary>
    /// Child name → file reference number for one directory. A directory we cannot open is one
    /// whose children we are about to skip anyway, so the failure is logged and answered with an
    /// empty map: identity is missing, never wrong.
    /// </summary>
    private Dictionary<string, ulong> SafeFileIds(string directory)
    {
        try
        {
            return DirectoryFileIds.ForChildren(directory);
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "Could not read file ids under {Directory}; entries will carry none.", directory);
            return [];
        }
    }

    public ulong? TryGetFileId(string absolutePath)
    {
        try
        {
            return DirectoryFileIds.ForPath(absolutePath);
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the file id of {Path}.", absolutePath);
            return null;
        }
    }

    private ScanEntry? TryBuildEntry(string path, string mountRoot, Dictionary<string, ulong> fileIds)
    {
        try
        {
            var info = new FileInfo(path);
            var attributes = info.Attributes;
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var relativePath = Path.GetRelativePath(mountRoot, path);
            var name = Path.GetFileName(path);

            return new ScanEntry(
                relativePath,
                name,
                isDirectory,
                isDirectory ? 0 : info.Length,
                info.CreationTimeUtc,
                info.LastWriteTimeUtc,
                attributes,
                fileIds.TryGetValue(name, out var frn) ? frn : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not stat {Path}; skipping.", path);
            return null;
        }
    }
}
