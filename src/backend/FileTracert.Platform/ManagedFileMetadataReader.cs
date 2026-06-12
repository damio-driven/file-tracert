using System.Collections.Concurrent;
using FileTracert.Contracts.Platform;
using Microsoft.Extensions.Logging;

namespace FileTracert.Platform;

/// <summary>
/// Reads file size/timestamps via <c>FileInfo</c> with bounded parallelism.
/// Missing or unreadable files are simply left out of the result.
/// </summary>
internal sealed class ManagedFileMetadataReader : IFileMetadataReader
{
    private readonly ILogger<ManagedFileMetadataReader> _logger;

    public ManagedFileMetadataReader(ILogger<ManagedFileMetadataReader> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, FileMetadata>> ReadAsync(
        string mountRoot,
        IReadOnlyCollection<string> relativePaths,
        CancellationToken ct)
    {
        var result = new ConcurrentDictionary<string, FileMetadata>(StringComparer.Ordinal);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(relativePaths, options, (relativePath, _) =>
        {
            try
            {
                var info = new FileInfo(Path.Combine(mountRoot, relativePath));
                if (info.Exists)
                {
                    result[relativePath] = new FileMetadata(
                        info.Length,
                        info.CreationTimeUtc,
                        info.LastWriteTimeUtc);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not read metadata for {Path}; skipping.", relativePath);
            }

            return ValueTask.CompletedTask;
        });

        return result;
    }
}
