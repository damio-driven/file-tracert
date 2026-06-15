using System.IO;

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
/// <param name="DriveType">GetDriveType result (Fixed/Removable/…); best-effort, Unknown when undetermined.</param>
/// <param name="HasPhysicalExtents">
/// <c>true</c>  — IOCTL confirmed ≥1 real disk extent (definitely a physical volume).<br/>
/// <c>false</c> — IOCTL ran but returned zero extents or the device couldn't be reached
///               after a successful handle open (virtual / cloud mount).<br/>
/// <c>null</c>  — the volume device handle could not be opened at all; the volume is not
///               exposed as a kernel device object (strong signal: cloud / network filesystem).
/// Defaults to <c>true</c> so callers that skip the probe (tests, legacy paths) are treated
/// as real disks and are never silently hidden.
/// </param>
/// <param name="PartitionTypeGuid">
/// GPT partition type GUID (lower-case, no braces) when resolvable; null otherwise. Used to
/// recognise ESP/MSR/WinRE system partitions. Best-effort — never required.
/// </param>
public sealed record ProbedVolume(
    string VolumeGuid,
    string? SerialNumber,
    string? Label,
    string FileSystem,
    bool IsRemovable,
    IReadOnlyList<string> MountPoints,
    long CapacityBytes,
    long FreeBytes,
    string? PhysicalDiskId,
    DriveType DriveType = DriveType.Unknown,
    bool? HasPhysicalExtents = true,
    string? PartitionTypeGuid = null);
