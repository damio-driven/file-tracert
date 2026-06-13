namespace FileTracert.Contracts.Platform;

/// <summary>
/// Port for browsing the <em>real</em> filesystem of a mounted volume during
/// setup, one level at a time (lazy). Implemented in Platform; resolves the
/// current mount internally from the volume GUID. Returns sub-folders only —
/// the setup picker does not need files.
/// </summary>
public interface IFileSystemBrowser
{
    /// <summary>
    /// Immediate sub-folders of <paramref name="relativePath"/> (empty = volume root)
    /// on the volume identified by <paramref name="volumeGuid"/>. Inaccessible
    /// folders are skipped, not thrown.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">The volume is not currently mounted.</exception>
    IReadOnlyList<FolderNode> ListFolders(string volumeGuid, string relativePath);
}
