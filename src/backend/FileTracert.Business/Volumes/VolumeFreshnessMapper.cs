using FileTracert.Contracts.Dtos;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Volumes;

/// <summary>
/// Pure <see cref="Volume"/> → DTO mapping for the read API. Keeps the freshness
/// rule in one place: an online volume reports live figures (<c>DataIsLive</c>),
/// an offline one reports a last-known snapshot (<c>IsStale</c>) — free/space are
/// the last values seen at <see cref="Volume.LastSeenUtc"/>. No DB/clock access,
/// so it is trivially unit-testable.
/// </summary>
public static class VolumeFreshnessMapper
{
    /// <summary>List/dashboard projection. <paramref name="fileCount"/> comes from an aggregate query.</summary>
    public static VolumeDto ToDto(Volume v, int fileCount) => new(
        v.Id,
        v.VolumeGuid,
        v.Label,
        v.LastDriveLetter,
        v.FileSystem,
        v.IsRemovable,
        v.IsOnline,
        v.LastSeenUtc,
        v.CapacityBytes,
        v.FreeBytesLastKnown,
        fileCount,
        v.LastFullScanUtc,
        DataIsLive: v.IsOnline,
        IsStale: !v.IsOnline);

    /// <summary>Detail projection: identity + monitored roots + index statistics.</summary>
    public static VolumeDetailDto ToDetailDto(
        Volume v,
        IReadOnlyList<WatchedRootDto> watchedRoots,
        int directoryCount,
        int fileCount,
        long indexedBytes) => new(
        v.Id,
        v.VolumeGuid,
        v.Label,
        v.LastDriveLetter,
        v.FileSystem,
        v.IsRemovable,
        v.IsOnline,
        v.LastSeenUtc,
        v.CapacityBytes,
        v.FreeBytesLastKnown,
        v.LastFullScanUtc,
        DataIsLive: v.IsOnline,
        IsStale: !v.IsOnline,
        v.SerialNumber,
        v.PhysicalDiskId,
        v.LastUsn,
        v.ScanEngine.ToString(),
        watchedRoots,
        directoryCount,
        fileCount,
        indexedBytes);
}
