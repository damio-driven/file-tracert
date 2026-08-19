namespace FileTracert.Contracts.Dtos;

/// <summary>
/// Read-only projection of a catalog volume for the list/dashboard views. Carries
/// the freshness flags the UI needs to tell live data from a last-known snapshot
/// of an offline volume: when <see cref="IsOnline"/> is false the free/space
/// figures are the last values seen (<see cref="LastSeenUtc"/>), not live.
/// <c>DataIsLive</c> is that statement, named for what it describes: whether the numbers on
/// this row were read live. It mirrors <see cref="IsOnline"/> today (§7 asks for one such flag;
/// the redundant <c>IsStale</c>, its literal negation, was a third field for the same bit).
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
    string Kind,
    bool IsCatalogable);
