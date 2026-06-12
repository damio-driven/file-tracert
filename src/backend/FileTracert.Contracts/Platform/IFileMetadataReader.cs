namespace FileTracert.Contracts.Platform;

/// <summary>Disk metadata for a file that the USN snapshot does not carry.</summary>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="CreatedUtc">Creation time (UTC).</param>
/// <param name="ModifiedUtc">Last write time (UTC).</param>
public sealed record FileMetadata(long SizeBytes, DateTime CreatedUtc, DateTime ModifiedUtc);

/// <summary>
/// Port for reading per-file metadata (size + timestamps) from disk. Implemented
/// in Platform so Business never touches <c>System.IO</c> directly. Used to fill
/// the size/dates the USN full snapshot leaves empty — only for files that pass
/// the filter, so we never pay the cost on discarded files.
/// </summary>
public interface IFileMetadataReader
{
    /// <summary>
    /// Reads metadata for each relative path resolved against <paramref name="mountRoot"/>.
    /// Files that cannot be read (gone/denied) are omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<string, FileMetadata>> ReadAsync(
        string mountRoot,
        IReadOnlyCollection<string> relativePaths,
        CancellationToken ct);
}
