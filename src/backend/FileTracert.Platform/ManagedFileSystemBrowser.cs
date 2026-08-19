using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Scanning;
using Microsoft.Extensions.Logging;

namespace FileTracert.Platform;

/// <summary>
/// BCL implementation of <see cref="IFileSystemBrowser"/>. Lists the immediate
/// sub-folders of one path on a mounted volume (lazy, one level), resolving the
/// current mount point from the volume GUID via <see cref="IVolumeProbe"/>.
/// Unreadable folders are skipped so a single denied child never aborts the listing.
/// </summary>
internal sealed class ManagedFileSystemBrowser : IFileSystemBrowser
{
    private readonly IVolumeProbe _probe;
    private readonly ILogger<ManagedFileSystemBrowser> _logger;

    public ManagedFileSystemBrowser(IVolumeProbe probe, ILogger<ManagedFileSystemBrowser> logger)
    {
        _probe = probe;
        _logger = logger;
    }

    public IReadOnlyList<FolderNode> ListFolders(string volumeGuid, string relativePath)
    {
        var probed = _probe.TryGetByGuid(volumeGuid)
            ?? throw new InvalidOperationException($"Volume {volumeGuid} is offline.");
        var mountRoot = probed.MountPoints.FirstOrDefault()
            ?? throw new InvalidOperationException($"Volume {volumeGuid} has no mount point.");

        // K7: normalization and volume-relative join are ScanPath's rules, not a third
        // hand-written copy — the paths this returns end up in WatchedRoots and are matched
        // against scan paths, so the two spellings have to be the same one.
        var normalized = ScanPath.Normalize(relativePath);
        var absolute = normalized.Length == 0 ? mountRoot : Path.Combine(mountRoot, normalized);

        var result = new List<FolderNode>();
        foreach (var dir in SafeEnumerateDirectories(absolute))
        {
            var name = Path.GetFileName(dir);
            var rel = ScanPath.Join(normalized, name);
            result.Add(new FolderNode(name, rel, HasSubDirectory(dir)));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private string[] SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Cannot list directories under {Directory}; returning none.", directory);
            return [];
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogDebug(ex, "Cannot list directories under {Directory}; returning none.", directory);
            return [];
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Cannot list directories under {Directory}; returning none.", directory);
            return [];
        }
    }

    private bool HasSubDirectory(string directory)
    {
        try
        {
            using var e = Directory.EnumerateDirectories(directory).GetEnumerator();
            return e.MoveNext();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            _logger.LogDebug(ex, "Cannot probe sub-folders of {Directory}; assuming none.", directory);
            return false;
        }
    }
}
