namespace FileTracert.Contracts.Platform;

/// <summary>
/// Port toward the OS for all file-system mutation operations used by the queue
/// processor. Implemented in Platform; Business never touches System.IO directly.
/// Paths are relative to the volume root. Volumes identified by GUID path
/// (e.g. <c>\\?\Volume{GUID}\</c>).
/// </summary>
public interface IFileMover
{
    /// <summary>Creates a directory (and all intermediate directories) on the given volume.</summary>
    void CreateFolder(string volumeGuid, string relativePath);

    /// <summary>Renames a file or directory in-place (intra-volume, metadata-only, instant).</summary>
    void RenameIntraVolume(string volumeGuid, string relativePath, string newName);

    /// <summary>Moves a file or directory to a new location on the same volume (metadata-only, instant).</summary>
    void MoveIntraVolume(string volumeGuid, string srcRel, string destRel);

    /// <summary>
    /// Copies a single file cross-volume, writing to <paramref name="destPartialRel"/>
    /// (a <c>.fadit-partial</c> temp path). <paramref name="onProgress"/> is awaited inline
    /// after each buffer write (not fire-and-forget like <see cref="IProgress{T}"/>, which
    /// posts through a captured <see cref="SynchronizationContext"/>/thread pool and would
    /// race the caller's own DB writes) — this lets the caller safely persist bytes-copied
    /// to a non-thread-safe resource such as an EF <c>DbContext</c> from the same async flow.
    /// Cancellation is honoured; the partial file is left on disk for possible resume.
    /// </summary>
    Task CopyFileAsync(string srcVolGuid, string srcRel,
                       string destVolGuid, string destPartialRel,
                       Func<long, CancellationToken, Task>? onProgress, CancellationToken ct);

    /// <summary>
    /// Returns <c>true</c> when the two files are identical in size (and optionally SHA-256 hash).
    /// </summary>
    bool Verify(string aVolGuid, string aRel, string bVolGuid, string bRel, bool withHash);

    /// <summary>
    /// Atomically renames a <c>.fadit-partial</c> file to its final name.
    /// </summary>
    /// <exception cref="NameCollisionException">The final path already exists.</exception>
    void FinalizePartial(string volGuid, string partialRel, string finalRel);

    /// <summary>Sends a file or directory to the Recycle Bin (never hard-deletes).</summary>
    void DeleteToRecycleBin(string volGuid, string relativePath);

    /// <summary>Creates all directories in <paramref name="relativePath"/> that do not yet exist.</summary>
    void EnsureTargetDirectory(string volGuid, string relativePath);
}
