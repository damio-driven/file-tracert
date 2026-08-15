using FileTracert.Business.Scanning;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Projection;

/// <summary>
/// The projected identity of a catalog row (§5): what the Catalog, the Search results and the
/// FTS index must show while an operation sits in the queue. Defined once, here.
///
/// Two readers cannot call these methods because they run inside SQL — the FTS
/// <c>INSERT … SELECT</c> statements (<c>FileTracert.Data/Search/FileSearchIndex.cs</c>) and the
/// EF projections of the Catalog controller. Both mirror the exact same rule as a
/// <c>COALESCE(NULLIF(PendingName, ''), Name)</c> / <c>PendingName ?? Name</c> and point back here.
/// </summary>
public static class Projected
{
    /// <summary>Projected file name: <c>PendingName ?? Name</c>.</summary>
    public static string NameOf(FileEntry file) => Pick(file.PendingName, file.Name);

    /// <summary>Projected directory name: <c>PendingName ?? Name</c>.</summary>
    public static string NameOf(DirectoryNode directory) => Pick(directory.PendingName, directory.Name);

    /// <summary>Projected directory of a file: <c>PendingDirectoryId ?? DirectoryId</c>.</summary>
    public static int DirectoryIdOf(FileEntry file) => file.PendingDirectoryId ?? file.DirectoryId;

    /// <summary>Projected parent of a directory: <c>PendingParentId ?? ParentId</c>.</summary>
    public static int? ParentIdOf(DirectoryNode directory) => directory.PendingParentId ?? directory.ParentId;

    /// <summary>
    /// The <c>path</c> column of the FTS index for a file: the <b>physical</b> directory path
    /// joined with the <b>projected</b> file name.
    ///
    /// The asymmetry is deliberate, not an oversight (§5: «un rename-cartella non tocca l'FTS»).
    /// A queued file rename must become findable under the new name at once, so the name column
    /// follows the overlay; a queued FOLDER rename changes no file name, and rewriting the path
    /// column for every file under it would mean tens of thousands of FTS writes per enqueue.
    /// A file under a folder with a pending rename is therefore still found by its OLD path in a
    /// full-path search, while the path SHOWN in the results is the projected one
    /// (<see cref="ProjectedPathResolver"/>).
    /// </summary>
    public static string FtsPath(string directoryMaterializedPath, string projectedFileName) =>
        ScanPath.Join(directoryMaterializedPath, projectedFileName);

    private static string Pick(string? pending, string physical) =>
        string.IsNullOrEmpty(pending) ? physical : pending;
}
