using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
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

    /// <summary>
    /// One page of the immediate sub-folders of <paramref name="path"/>, in name order.
    /// Step 17: the disk decides how many folders a level holds (a package cache has hundreds),
    /// so the answer is paged like every other list of §7. The enumeration itself is not — the
    /// browser must read the whole level to sort it — but the payload, and what the tree has to
    /// render, is bounded by the page.
    /// </summary>
    public async Task<PagedResult<FolderNodeDto>> ListAsync(int volumeId, string path, PagedRequest paged, CancellationToken ct)
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

        var all = _browser.ListFolders(volume.VolumeGuid, normalized);
        var page = all
            .Skip(paged.Skip)
            .Take(paged.Take)
            .Select(n => new FolderNodeDto(n.Name, n.RelativePath, n.HasChildren))
            .ToList();
        return new PagedResult<FolderNodeDto>(page, all.Count, paged.Skip, paged.Take);
    }
}
