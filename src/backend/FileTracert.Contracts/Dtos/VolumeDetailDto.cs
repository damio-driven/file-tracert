namespace FileTracert.Contracts.Dtos;

/// <summary>
/// Everything the Volumi detail panel shows: the list fields plus the technical
/// identity (serial, physical disk, USN checkpoint), the monitored roots and the
/// index statistics. Carries the same freshness flags as <see cref="VolumeDto"/>.
/// </summary>
public sealed record VolumeDetailDto(
    int Id,
    string VolumeGuid,
    string? Label,
    string? CurrentLetter,
    string FileSystem,
    bool IsRemovable,
    bool IsOnline,
    DateTime LastSeenUtc,
    long CapacityBytes,
    long FreeBytes,
    DateTime? LastFullScanUtc,
    bool DataIsLive,
    string Kind,
    bool IsCatalogable,
    // --- technical identity ---
    string? SerialNumber,
    string? PhysicalDiskId,
    long? LastUsn,
    string ScanEngine,
    // --- monitored roots ---
    IReadOnlyList<WatchedRootDto> WatchedRoots,
    // --- index statistics ---
    int DirectoryCount,
    int FileCount,
    long IndexedBytes);
