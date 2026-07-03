using FileTracert.Contracts.Platform;

namespace FileTracert.HardwareSmoke;

/// <summary>Resolves an absolute path to the (volume GUID, volume-relative path) the mover needs.</summary>
public interface IVolumePathResolver
{
    (string VolumeGuid, string RelativePath) Resolve(string absolutePath);
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

    public (string VolumeGuid, string RelativePath) Resolve(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        foreach (var (guid, mount) in _mounts)
        {
            if (full.StartsWith(mount, StringComparison.OrdinalIgnoreCase))
                return (guid, Path.GetRelativePath(mount, full));
        }
        throw new InvalidOperationException($"No mounted volume contains path '{full}'.");
    }
}
