using FileTracert.Contracts.Enums;

namespace FileTracert.Data.Entities;

/// <summary>Directory entity. Mapped to table "Directories" to avoid the System.IO.Directory collision.</summary>
public class DirectoryNode : IAuditable
{
    public int Id { get; set; }
    public int VolumeId { get; set; }
    public int? ParentId { get; set; }
    public string Name { get; set; } = null!;

    /// <summary>Denormalized path relative to volume root, updated in cascade on rename.</summary>
    public string MaterializedPath { get; set; } = null!;

    public long? UsnFileRef { get; set; }
    public bool IsMaterialized { get; set; }

    /// <summary>
    /// Mirror of <see cref="FileEntry.IsPresent"/>: false once a scan no longer finds the
    /// directory on disk. Soft marker, never a delete — deleting the row would take the
    /// pending overlay of every descendant with it (§6, no hard-delete).
    /// Defaults to <c>true</c> in CLR too: every construction site (scan, IndexUpdater,
    /// harness) builds the node without naming the flag, and "false" would mean
    /// "not on disk" for rows that were just indexed from disk.
    /// </summary>
    public bool IsPresent { get; set; } = true;

    /// <summary>
    /// Step 18: this folder, or an ancestor, is Hidden/System — as the last full scan or USN
    /// delta saw it. EFFECTIVE, not own: the USN delta reads it off the parent row to inherit an
    /// exclusion the record itself does not carry (a file inside a hidden folder has clean
    /// attributes of its own). Only a scan that WALKS the folder clears it (<c>DirectoryMerger</c>);
    /// no setting can, because no setting knows whether the folder is still hidden.
    /// Not an inclusion flag: a folder that exists on disk exists (11g), visibility is unchanged.
    /// <para>The only cause a directory row carries. The PATH cause needs no column: the delta
    /// evaluates <c>IsPathExcluded</c> on every segment of an item's own path, so that one is
    /// inherited by construction; the attribute is the fact only the disk knows.</para>
    /// </summary>
    public bool ExcludedByScan { get; set; }

    public string? PendingName { get; set; }
    public int? PendingParentId { get; set; }
    public EntityPendingState PendingState { get; set; }
    public int? PendingJobId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Volume Volume { get; set; } = null!;
    public DirectoryNode? Parent { get; set; }
    public ICollection<DirectoryNode> Children { get; set; } = new List<DirectoryNode>();
    public ICollection<FileEntry> Files { get; set; } = new List<FileEntry>();
}
