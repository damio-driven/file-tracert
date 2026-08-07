using FileTracert.Contracts.Platform;

namespace FileTracert.HardwareSmoke;

/// <summary>An absolute path resolved onto the volume that hosts it.</summary>
/// <param name="VolumeGuid">Volume GUID path (<c>\\?\Volume{GUID}\</c>).</param>
/// <param name="MountPoint">The mount the path was resolved against (e.g. <c>E:\</c>).</param>
/// <param name="RelativePath">The path relative to that mount — the form the queue and DB use.</param>
public sealed record ResolvedPath(string VolumeGuid, string MountPoint, string RelativePath);

/// <summary>Resolves an absolute path to the (volume GUID, volume-relative path) the mover needs.</summary>
public interface IVolumePathResolver
{
    /// <summary>Resolves <paramref name="absolutePath"/> or throws when no mounted volume hosts it.</summary>
    ResolvedPath Resolve(string absolutePath);
}

/// <summary>
/// Production resolver backed by the <see cref="IVolumeProbe"/> port: finds the mounted volume
/// whose mount point is the longest prefix of the absolute path (so a nested mount wins over its
/// parent) and returns the path relative to that mount.
/// </summary>
public sealed class VolumePathResolver : IVolumePathResolver
{
    private readonly IReadOnlyList<(string Guid, string Mount)> _mounts;

    public VolumePathResolver(IVolumeProbe probe)
    {
        _mounts = probe.EnumerateVolumes()
            .Where(v => v.MountPoints.Count > 0)
            .SelectMany(v => v.MountPoints.Select(m => (v.VolumeGuid, Mount: m)))
            .OrderByDescending(x => x.Mount.Length)
            .ToList();
    }

    public ResolvedPath Resolve(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        foreach (var (guid, mount) in _mounts)
        {
            if (full.StartsWith(mount, StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(mount, full);
                // GetRelativePath returns "." when the path IS the mount; the queue's convention
                // for "volume root" is the empty string.
                return new ResolvedPath(guid, mount, relative == "." ? string.Empty : relative);
            }
        }

        throw new InvalidOperationException($"No mounted volume contains path '{full}'.");
    }
}
