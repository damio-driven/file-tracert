using FileTracert.Contracts.Platform;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke;

/// <summary>
/// Reads the absolute paths of the production catalogue's active WatchedRoots so the guard-rails
/// can refuse to run the smoke harness anywhere near catalogued data.
/// </summary>
public static class ProductionRootsReader
{
    /// <summary>
    /// Result of reading the production DB.
    /// <see cref="CouldVerify"/> is false when a DB file exists but could not be read: the caller
    /// MUST then refuse to run (we cannot prove the target areas are clear of production data).
    /// A missing DB file is a genuine "no production install" → <c>CouldVerify=true</c>, empty list.
    /// </summary>
    public sealed record Result(bool CouldVerify, IReadOnlyList<string> RootPaths);

    public static Result Read(string mainDbPath, IVolumeProbe probe)
    {
        if (!File.Exists(mainDbPath))
            return new Result(CouldVerify: true, []);

        try
        {
            var options = new DbContextOptionsBuilder<FileTracertDbContext>()
                .UseSqlite($"Data Source={mainDbPath}")
                .Options;
            using var db = new FileTracertDbContext(options);

            var roots = db.WatchedRoots
                .Where(w => w.IsActive)
                .Select(w => new { w.RelativePath, w.Volume.VolumeGuid })
                .AsNoTracking()
                .ToList();

            var mounts = probe.EnumerateVolumes()
                .Where(v => v.MountPoints.Count > 0)
                .ToDictionary(v => v.VolumeGuid, v => v.MountPoints[0], StringComparer.OrdinalIgnoreCase);

            var result = new List<string>();
            foreach (var r in roots)
                if (mounts.TryGetValue(r.VolumeGuid, out var mount))
                    result.Add(Path.GetFullPath(Path.Combine(mount, r.RelativePath)));

            return new Result(CouldVerify: true, result);
        }
        catch
        {
            // Non-silent at the call site: the DB exists but is unreadable → cannot verify.
            return new Result(CouldVerify: false, []);
        }
    }
}
