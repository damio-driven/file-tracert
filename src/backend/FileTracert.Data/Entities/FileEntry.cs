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

    /// <summary>
    /// The row is part of the catalog the user asked for. Derived, and kept in step with the three
    /// cause flags below by every writer: <c>IsIncluded == !(ExcludedByType || ExcludedByRoot ||
    /// ExcludedByScan)</c>. It stays a column of its own because it is what the Catalog, the search
    /// index and the covering indexes read — one boolean instead of three ORed at every seek.
    /// </summary>
    public bool IsIncluded { get; set; }

    /// <summary>
    /// Excluded because the extension is outside the allow-list. Undone by <c>FilterReconciler</c>
    /// the moment the filter widens — no scan needed, the extension is right there on the row.
    /// </summary>
    public bool ExcludedByType { get; set; }

    /// <summary>
    /// Excluded because no ACTIVE watched root governs it: the root was switched off or removed.
    /// Undone by <c>FilterReconciler</c> when the root comes back — again with no scan, because
    /// "is this root active" is a fact of the settings, not of the disk.
    /// </summary>
    public bool ExcludedByRoot { get; set; }

    /// <summary>
    /// Excluded because the scan itself stepped over it: its attributes (Hidden/System), an
    /// excluded segment in its path, or a folder above it that failed one of those rules.
    ///
    /// <para>This is the cause reconciliation must NOT undo, and the reason the causes are
    /// persisted at all (step 11h). Nothing in Setup can know whether that folder is still hidden;
    /// only a scan can, and the merge clears the flag when it sees the file again.</para>
    /// </summary>
    public bool ExcludedByScan { get; set; }

    /// <summary>
    /// The file exists ON DISK. False only for a row a queued Copy has projected at its
    /// destination (step 15a): §5 says queuing an operation mutates the projection at once, and a
    /// copy is the one operation whose result is a NEW entity — there is no existing row whose
    /// <c>Pending*</c> fields could carry it, so the destination row is created ahead of the file,
    /// exactly as <see cref="DirectoryNode.IsMaterialized"/> already does for folders.
    ///
    /// <para>Distinct from <see cref="IsPresent"/> on purpose, and the two are not
    /// interchangeable: <c>IsPresent = false</c> means "a scan looked for it and did not find it"
    /// (§6), a statement about the disk that only a scan may write. <c>IsMaterialized = false</c>
    /// means "nothing has created it yet" — no scan has ever looked. A projected row carries both
    /// as false and is promoted to both true by the job that lands the bytes.</para>
    ///
    /// <para>Defaults to <c>true</c> — in the CLR and in the column — because every other writer
    /// of this table describes a file it has just seen on disk (the scan merge, the USN delta,
    /// the bulk insert). Only the projection deliberately says otherwise, and it says so
    /// explicitly.</para>
    /// </summary>
    public bool IsMaterialized { get; set; } = true;

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
