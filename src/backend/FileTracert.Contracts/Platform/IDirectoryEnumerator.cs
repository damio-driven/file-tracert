namespace FileTracert.Contracts.Platform;

/// <summary>
/// Port for the BCL directory-enumeration scan engine (fallback for volumes
/// without a USN journal). Implemented in Platform. No diffing here — just
/// enumeration; the diff against the DB is Business's job.
/// </summary>
public interface IDirectoryEnumerator
{
    /// <summary>
    /// Enumerates every file and directory under <paramref name="relativeRoot"/>,
    /// resolved against the current <paramref name="mountRoot"/>. Paths in the
    /// returned entries are relative to the volume root.
    /// </summary>
    /// <param name="mountRoot">Current mount point of the volume (e.g. <c>E:\</c>).</param>
    /// <param name="relativeRoot">Watched root relative to the volume (empty for the whole volume).</param>
    /// <param name="ct">Cancellation token, honored during the walk.</param>
    IEnumerable<ScanEntry> Enumerate(string mountRoot, string relativeRoot, CancellationToken ct);

    /// <summary>
    /// The file reference number of one object named by its absolute path, or null when the
    /// filesystem has none for it or it cannot be read.
    /// </summary>
    /// <remarks>
    /// The walk starts <em>inside</em> a watched root and never yields the root itself, yet the
    /// incremental path resolves records whose parent is exactly that directory. Its identity
    /// therefore has to be askable on its own, once per root, instead of being a by-product of a
    /// walk that does not visit it.
    /// </remarks>
    ulong? TryGetFileId(string absolutePath);
}
