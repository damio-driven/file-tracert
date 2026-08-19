using FileTracert.Contracts.Dtos;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Volumes;

/// <summary>
/// Pure <see cref="Volume"/> → DTO mapping for the read API. Keeps the freshness
/// rule in one place: <c>DataIsLive</c> says whether the figures on this volume were read
/// live; when it is false they are the last values seen at <see cref="Volume.LastSeenUtc"/>.
/// One flag, not two — the old <c>IsStale</c> was its literal negation, a third field carrying
/// the same bit as <see cref="Volume.IsOnline"/> and read by nothing (K13). No DB/clock access,
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
        v.Kind.ToString(),
        v.IsCatalogable);

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
        v.Kind.ToString(),
        v.IsCatalogable,
        v.SerialNumber,
        v.PhysicalDiskId,
        v.LastUsn,
        v.ScanEngine.ToString(),
        watchedRoots,
        directoryCount,
        fileCount,
        indexedBytes);
}
