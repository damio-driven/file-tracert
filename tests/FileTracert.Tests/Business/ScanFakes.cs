using System.ComponentModel;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Search;

namespace FileTracert.Tests.Business;

/// <summary>Records every tracker call so scan-progress hooks can be asserted.</summary>
internal sealed class RecordingScanStatusTracker : IScanStatusTracker
{
    public List<int> Begun { get; } = [];
    public List<ScanPhase> Phases { get; } = [];
    public List<int> Completed { get; } = [];
    public List<int> Failed { get; } = [];
    public long LastWritten { get; private set; } = -1;

    public void Begin(int volumeId, string? label) => Begun.Add(volumeId);
    public void SetPhase(int volumeId, ScanPhase phase) => Phases.Add(phase);
    public void ReportSeen(int volumeId, long itemsSeen, string? currentRoot = null) { }
    public void ReportWritten(int volumeId, long itemsWritten) => LastWritten = itemsWritten;
    public void Complete(int volumeId) => Completed.Add(volumeId);
    public void Fail(int volumeId) => Failed.Add(volumeId);
    public IReadOnlyList<ScanStatusDto> Snapshot() => [];
}

/// <summary>Captures notifications published during a scan for assertions.</summary>
internal sealed class FakeNotificationPublisher : INotificationPublisher
{
    public List<(NotificationSeverity Severity, string Source, string Title, string Message, int? VolumeId)> Published { get; } = [];

    public Task PublishAsync(
        NotificationSeverity severity,
        string source,
        string title,
        string message,
        int? volumeId,
        CancellationToken ct)
    {
        Published.Add((severity, source, title, message, volumeId));
        return Task.CompletedTask;
    }
}

/// <summary>Hand-written port fakes for ScanService integration tests.</summary>
internal sealed class FakeVolumeProbe(ProbedVolume volume) : IVolumeProbe
{
    public IReadOnlyList<ProbedVolume> EnumerateVolumes() => [volume];

    public ProbedVolume? TryGetByGuid(string volumeGuid) =>
        string.Equals(volumeGuid, volume.VolumeGuid, StringComparison.OrdinalIgnoreCase) ? volume : null;

    public long? TryGetFreeBytes(string volumeGuid) => TryGetByGuid(volumeGuid)?.FreeBytes;
}

internal sealed class FakeVolumesProbe(IReadOnlyList<ProbedVolume> volumes) : IVolumeProbe
{
    public IReadOnlyList<ProbedVolume> EnumerateVolumes() => volumes;

    public ProbedVolume? TryGetByGuid(string volumeGuid) =>
        volumes.FirstOrDefault(v => string.Equals(v.VolumeGuid, volumeGuid, StringComparison.OrdinalIgnoreCase));

    public long? TryGetFreeBytes(string volumeGuid) => TryGetByGuid(volumeGuid)?.FreeBytes;
}

internal sealed class FakeDirectoryEnumerator(IReadOnlyList<ScanEntry> entries) : IDirectoryEnumerator
{
    public IEnumerable<ScanEntry> Enumerate(string mountRoot, string relativeRoot, CancellationToken ct) => entries;
}

/// <summary>Enumerator that blows up, to exercise the scan failure path.</summary>
internal sealed class ThrowingDirectoryEnumerator : IDirectoryEnumerator
{
    public IEnumerable<ScanEntry> Enumerate(string mountRoot, string relativeRoot, CancellationToken ct) =>
        throw new IOException("Enumeration failed.");
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

/// <summary>NTFS volume whose journal cannot be created/queried (e.g. not active, no elevation).</summary>
internal sealed class ThrowingUsnReader : IUsnReader
{
    public bool SupportsUsn(string volumeGuid) => true;

    public UsnJournalState GetJournalState(string volumeGuid) =>
        throw new Win32Exception(1179, "FSCTL_QUERY_USN_JOURNAL failed.");

    public void EnsureJournal(string volumeGuid) =>
        throw new Win32Exception(1179, "FSCTL_CREATE_USN_JOURNAL failed (requires elevation).");

    public IEnumerable<UsnEntry> ReadFullSnapshot(string volumeGuid, CancellationToken ct) => [];

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

/// <summary>No-op FTS index for unit tests that do not exercise search.</summary>
internal sealed class FakeFileSearchIndex : IFileSearchIndex
{
    public Task ClearVolumeAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
    public Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
    public Task RebuildAsync(CancellationToken ct) => Task.CompletedTask;
    public Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct) => Task.CompletedTask;
    public Task SyncDirectoriesAsync(IReadOnlyCollection<int> directoryIds, CancellationToken ct) => Task.CompletedTask;
    public Task PruneVolumeAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
    public Task UpsertAsync(int fileId, string name, string path, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveAsync(int fileId, CancellationToken ct) => Task.CompletedTask;
    public Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct)
        => Task.FromResult(new PagedResult<int>([], 0, query.Skip, query.Take));
}
