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
