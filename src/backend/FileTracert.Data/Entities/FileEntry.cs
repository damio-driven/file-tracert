using FileTracert.Contracts.Enums;

namespace FileTracert.Data.Entities;

/// <summary>
/// File entity. Mapped to table "Files" to avoid the System.IO.File collision.
/// NOTE on audit fields: the file has its own timestamps (creation/modification on
/// disk) AND row-audit timestamps from <see cref="IAuditable"/>. To avoid a name
/// clash, the file's own dates are <see cref="FileCreatedUtc"/> / <see cref="FileModifiedUtc"/>
/// (mapped to DB columns CreatedUtc / ModifiedUtc), while the IAuditable
/// CreatedUtc / UpdatedUtc map to DB columns RowCreatedUtc / RowUpdatedUtc.
/// </summary>
public class FileEntry : IAuditable
{
    public int Id { get; set; }
    public int VolumeId { get; set; }
    public int DirectoryId { get; set; }
    public string Name { get; set; } = null!;

    /// <summary>Lower-cased extension (without the leading dot).</summary>
    public string Extension { get; set; } = null!;

    public FileCategory Category { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>The file's own creation date on disk (DB column "CreatedUtc").</summary>
    public DateTime FileCreatedUtc { get; set; }

    /// <summary>The file's own modification date on disk (DB column "ModifiedUtc").</summary>
    public DateTime FileModifiedUtc { get; set; }

    public System.IO.FileAttributes Attributes { get; set; }
    public long? UsnFileRef { get; set; }

    /// <summary>Quick hash: size + first/last KB.</summary>
    public string? QuickHash { get; set; }

    /// <summary>Full hash (lazy).</summary>
    public string? Hash { get; set; }

    public bool IsIncluded { get; set; }
    public bool IsPresent { get; set; }
    public DateTime LastIndexedUtc { get; set; }

    public string? PendingName { get; set; }
    public int? PendingDirectoryId { get; set; }
    public EntityPendingState PendingState { get; set; }
    public int? PendingJobId { get; set; }

    /// <summary>Row-audit creation (IAuditable, DB column "RowCreatedUtc").</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Row-audit update (IAuditable, DB column "RowUpdatedUtc").</summary>
    public DateTime UpdatedUtc { get; set; }

    public Volume Volume { get; set; } = null!;
    public DirectoryNode Directory { get; set; } = null!;
}
