using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Setup;

/// <summary>Raised when a setup action targets a volume that is not currently mounted.</summary>
public sealed class VolumeOfflineException(int volumeId)
    : Exception($"Volume {volumeId} is offline.")
{
    public int VolumeId { get; } = volumeId;
}

/// <summary>Raised when an untrusted path fails server-side validation.</summary>
public sealed class InvalidPathException(string reason) : Exception(reason);

/// <summary>
/// Browses the real filesystem of an online volume for the setup picker. Validates
/// the untrusted path (Business never touches <c>System.IO</c> — it goes through
/// the <see cref="IFileSystemBrowser"/> port).
/// </summary>
public sealed class FolderBrowseService
{
    private readonly FileTracertDbContext _db;
    private readonly IVolumeProbe _probe;
    private readonly IFileSystemBrowser _browser;

    public FolderBrowseService(FileTracertDbContext db, IVolumeProbe probe, IFileSystemBrowser browser)
    {
        _db = db;
        _probe = probe;
        _browser = browser;
    }

    public async Task<IReadOnlyList<FolderNodeDto>> ListAsync(int volumeId, string path, CancellationToken ct)
    {
        var volume = await _db.Volumes.AsNoTracking().FirstOrDefaultAsync(v => v.Id == volumeId, ct)
            ?? throw new KeyNotFoundException($"Volume {volumeId} not found.");

        if (!WatchedRootPath.TryValidate(path, out var normalized, out var error))
        {
            throw new InvalidPathException(error);
        }

        if (_probe.TryGetByGuid(volume.VolumeGuid) is null)
        {
            throw new VolumeOfflineException(volumeId);
        }

        return _browser.ListFolders(volume.VolumeGuid, normalized)
            .Select(n => new FolderNodeDto(n.Name, n.RelativePath, n.HasChildren))
            .ToList();
    }
}
