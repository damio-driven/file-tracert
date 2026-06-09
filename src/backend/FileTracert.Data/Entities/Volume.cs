using FileTracert.Contracts.Enums;

namespace FileTracert.Data.Entities;

public class Volume : IAuditable
{
    public int Id { get; set; }

    /// <summary>Volume GUID path (\\?\Volume{GUID}\) — stable identity, the real key.</summary>
    public string VolumeGuid { get; set; } = null!;

    public string? SerialNumber { get; set; }
    public string? Label { get; set; }
    public string FileSystem { get; set; } = null!;
    public bool IsRemovable { get; set; }
    public string? PhysicalDiskId { get; set; }
    public string? LastDriveLetter { get; set; }
    public long CapacityBytes { get; set; }
    public long FreeBytesLastKnown { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool IsOnline { get; set; }
    public VolumeScanEngine ScanEngine { get; set; }
    public long? LastUsn { get; set; }
    public DateTime? LastFullScanUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ICollection<WatchedRoot> WatchedRoots { get; set; } = new List<WatchedRoot>();
    public ICollection<DirectoryNode> Directories { get; set; } = new List<DirectoryNode>();
    public ICollection<FileEntry> Files { get; set; } = new List<FileEntry>();
}
