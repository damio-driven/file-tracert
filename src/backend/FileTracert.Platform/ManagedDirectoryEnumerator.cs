using FileTracert.Contracts.Platform;
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

            foreach (var path in SafeEnumerate(directory))
            {
                var entry = TryBuildEntry(path, mountRoot);
                if (entry is null)
                {
                    continue;
                }

                if (entry.IsDirectory)
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
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Access denied, skipping {Directory}.", directory);
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogDebug("Directory vanished mid-scan, skipping {Directory}.", directory);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "I/O error enumerating {Directory}; skipping.", directory);
        }

        return [];
    }

    private ScanEntry? TryBuildEntry(string path, string mountRoot)
    {
        try
        {
            var info = new FileInfo(path);
            var attributes = info.Attributes;
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var relativePath = Path.GetRelativePath(mountRoot, path);

            return new ScanEntry(
                relativePath,
                Path.GetFileName(path),
                isDirectory,
                isDirectory ? 0 : info.Length,
                info.CreationTimeUtc,
                info.LastWriteTimeUtc,
                attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not stat {Path}; skipping.", path);
            return null;
        }
    }
}
