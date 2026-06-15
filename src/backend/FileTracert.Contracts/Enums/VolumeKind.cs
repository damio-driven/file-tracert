namespace FileTracert.Contracts.Enums;

/// <summary>
/// Classification of a volume, used to decide whether it is catalogable by default.
/// Cloud/virtual mounts (Google Drive File Stream, OneDrive) and system partitions
/// (ESP, MSR, WinRE) are excluded; real fixed/removable disks are catalogued.
/// <c>Unknown</c> is deliberately catalogable — we never hide what we don't understand.
/// </summary>
public enum VolumeKind
{
    Unknown = 0,
    Fixed,
    Removable,
    Cloud,
    System,
}
