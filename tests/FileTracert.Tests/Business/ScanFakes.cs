using FileTracert.Contracts.Platform;

namespace FileTracert.Tests.Business;

/// <summary>Hand-written port fakes for ScanService integration tests.</summary>
internal sealed class FakeVolumeProbe(ProbedVolume volume) : IVolumeProbe
{
    public IReadOnlyList<ProbedVolume> EnumerateVolumes() => [volume];

    public ProbedVolume? TryGetByGuid(string volumeGuid) =>
        string.Equals(volumeGuid, volume.VolumeGuid, StringComparison.OrdinalIgnoreCase) ? volume : null;
}

internal sealed class FakeVolumesProbe(IReadOnlyList<ProbedVolume> volumes) : IVolumeProbe
{
    public IReadOnlyList<ProbedVolume> EnumerateVolumes() => volumes;

    public ProbedVolume? TryGetByGuid(string volumeGuid) =>
        volumes.FirstOrDefault(v => string.Equals(v.VolumeGuid, volumeGuid, StringComparison.OrdinalIgnoreCase));
}

internal sealed class FakeDirectoryEnumerator(IReadOnlyList<ScanEntry> entries) : IDirectoryEnumerator
{
    public IEnumerable<ScanEntry> Enumerate(string mountRoot, string relativeRoot, CancellationToken ct) => entries;
}

internal sealed class FakeUsnReader(IReadOnlyList<UsnEntry> entries, long nextUsn, ulong journalId = 1)
    : IUsnReader
{
    public bool SupportsUsn(string volumeGuid) => true;

    public UsnJournalState GetJournalState(string volumeGuid) =>
        new(journalId, FirstUsn: 0, NextUsn: nextUsn, LowestValidUsn: 0);

    public void EnsureJournal(string volumeGuid) { }

    public IEnumerable<UsnEntry> ReadFullSnapshot(string volumeGuid, CancellationToken ct) => entries;

    public UsnChangeResult ReadChanges(string volumeGuid, long sinceUsn, ulong journalId, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeFileMetadataReader(IReadOnlyDictionary<string, FileMetadata> map) : IFileMetadataReader
{
    public Task<IReadOnlyDictionary<string, FileMetadata>> ReadAsync(
        string mountRoot,
        IReadOnlyCollection<string> relativePaths,
        CancellationToken ct) => Task.FromResult(map);
}

internal sealed class FakeFileSystemBrowser(
    IReadOnlyDictionary<string, IReadOnlyList<FolderNode>> byPath) : IFileSystemBrowser
{
    public IReadOnlyList<FolderNode> ListFolders(string volumeGuid, string relativePath) =>
        byPath.TryGetValue(relativePath, out var folders) ? folders : [];
}
