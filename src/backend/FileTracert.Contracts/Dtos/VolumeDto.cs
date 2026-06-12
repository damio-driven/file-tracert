namespace FileTracert.Contracts.Dtos;

/// <summary>
/// Read-only projection of a catalog volume for the diagnostic API. Carries the
/// freshness fields the UI needs to tell live data from a last-known snapshot of
/// an offline volume.
/// </summary>
public sealed record VolumeDto(
    int Id,
    string VolumeGuid,
    string? SerialNumber,
    string? Label,
    string FileSystem,
    bool IsRemovable,
    string? PhysicalDiskId,
    string? LastDriveLetter,
    long CapacityBytes,
    long FreeBytesLastKnown,
    DateTime LastSeenUtc,
    bool IsOnline,
    string ScanEngine,
    long? LastUsn,
    DateTime? LastFullScanUtc);
