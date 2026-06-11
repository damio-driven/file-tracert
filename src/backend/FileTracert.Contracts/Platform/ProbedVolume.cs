namespace FileTracert.Contracts.Platform;

/// <summary>
/// Live snapshot of a mountable volume as seen on the system right now.
/// The mapping onto the <c>Volume</c> entity (LastSeenUtc, IsOnline,
/// FreeBytesLastKnown, …) is done later by a Business service — Platform never
/// touches the DB.
/// </summary>
/// <param name="VolumeGuid">Volume GUID path (<c>\\?\Volume{GUID}\</c>) — the stable key.</param>
/// <param name="SerialNumber">Volume serial (secondary signal, never the key).</param>
/// <param name="Label">Volume label, when available.</param>
/// <param name="FileSystem">Filesystem name (NTFS, exFAT, FAT32…); empty when the volume is not ready.</param>
/// <param name="IsRemovable">True when the drive type reports removable media.</param>
/// <param name="MountPoints">Current letters/paths (0..N — empty is normal, not an error).</param>
/// <param name="CapacityBytes">Total capacity in bytes; 0 when not available.</param>
/// <param name="FreeBytes">Free bytes; 0 when not available.</param>
/// <param name="PhysicalDiskId">Descriptive physical disk id; null when topology resolution failed.</param>
public sealed record ProbedVolume(
    string VolumeGuid,
    string? SerialNumber,
    string? Label,
    string FileSystem,
    bool IsRemovable,
    IReadOnlyList<string> MountPoints,
    long CapacityBytes,
    long FreeBytes,
    string? PhysicalDiskId);
