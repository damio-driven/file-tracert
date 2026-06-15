namespace FileTracert.Contracts.Dtos;

/// <summary>
/// Read-only projection of a catalog volume for the list/dashboard views. Carries
/// the freshness flags the UI needs to tell live data from a last-known snapshot
/// of an offline volume: when <see cref="IsOnline"/> is false the free/space
/// figures are the last values seen (<see cref="LastSeenUtc"/>), not live.
/// <c>DataIsLive</c> mirrors <see cref="IsOnline"/>; <c>IsStale</c> is its inverse.
/// </summary>
public sealed record VolumeDto(
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
    int FileCount,
    DateTime? LastFullScanUtc,
    bool DataIsLive,
    bool IsStale,
    string Kind,
    bool IsCatalogable);
