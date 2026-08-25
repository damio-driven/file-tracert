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

    /// <summary>How the volume was classified (cloud/system/fixed/removable/unknown).</summary>
    public VolumeKind Kind { get; set; }

    /// <summary>
    /// Whether the volume is eligible for cataloguing. Defaults from <see cref="Kind"/>
    /// (cloud/system → false) but the user can override it; the sync preserves a manual
    /// override (see <c>VolumeSyncService</c>).
    /// </summary>
    public bool IsCatalogable { get; set; } = true;

    public string? PhysicalDiskId { get; set; }
    public string? LastDriveLetter { get; set; }
    public long CapacityBytes { get; set; }
    public long FreeBytesLastKnown { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool IsOnline { get; set; }
    public VolumeScanEngine ScanEngine { get; set; }
    public long? LastUsn { get; set; }

    /// <summary>
    /// Instance id of the USN change journal <see cref="LastUsn"/> was taken from, stored as the
    /// signed reinterpretation of the native <c>ulong</c> (the same trick as
    /// <see cref="FileEntry.UsnFileRef"/>: SQLite has no unsigned 64-bit type, and the value is
    /// only ever compared for equality).
    /// <para>A cursor is meaningless without it. Deleting and recreating a journal (or a volume
    /// snapshot restore) restarts the numbering with a NEW id, so a <see cref="LastUsn"/> that
    /// looks perfectly valid can point into a completely different history. The incremental read
    /// hands both to <c>IUsnReader.ReadChanges</c>, which answers "continue" or "this is not the
    /// journal you were reading — rescan".</para>
    /// <para>Null on volumes indexed before this column existed, and on every non-NTFS volume:
    /// it is populated by the first scan that checkpoints a journal position.</para>
    /// </summary>
    public long? UsnJournalId { get; set; }

    public DateTime? LastFullScanUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ICollection<WatchedRoot> WatchedRoots { get; set; } = new List<WatchedRoot>();
    public ICollection<DirectoryNode> Directories { get; set; } = new List<DirectoryNode>();
    public ICollection<FileEntry> Files { get; set; } = new List<FileEntry>();
}
