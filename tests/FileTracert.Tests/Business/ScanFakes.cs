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
    /// <summary>
    /// Honours <paramref name="relativeRoot"/> the way the real enumerator does — it walks that
    /// subtree and nothing else. With a single root over the whole volume ("") this is the same
    /// list as before; with two roots it is the difference between a fixture and a fiction,
    /// because the scan calls this once PER root and would otherwise index every entry twice.
    /// </summary>
    public IEnumerable<ScanEntry> Enumerate(string mountRoot, string relativeRoot, CancellationToken ct) =>
        entries.Where(e => ScanPath.IsWithin(e.RelativePath, ScanPath.Normalize(relativeRoot)));
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

/// <summary>
/// A journal that can be driven from a test: a full snapshot for the scan, then a scripted delta
/// for the incremental pass. Records what cursor it was asked to resume from, which is how the
/// checkpoint assertions can tell "read the delta again" from "read on from where it stopped".
///
/// <para><b>Every mutable member is behind one lock, and the delta and its tail move TOGETHER.</b>
/// The worker tests hand this object to a LIVE <c>UsnSyncWorker</c> and then rewrite it from the
/// test thread — "the journal moved on" is expressed while the worker is running. A lock PER
/// PROPERTY does not survive that, and it was the first thing tried: two setters are two critical
/// sections, so the worker can take the lock BETWEEN them and compose an answer out of the new
/// records and the OLD tail, then checkpoint that cursor. The failure has a signature —
/// <c>LastUsn</c> one increment behind what the test wrote — and it is a red that appears under
/// load and nowhere else, which is the worst kind this suite can produce: it looks like the product
/// and it is the fixture. So the pair is not settable at all; <see cref="Script"/> writes both
/// under one lock and is the only door. <c>Resumed</c> is the same problem in the other direction
/// and is handed out as a copy.</para>
///
/// <para><c>JournalId</c> and <c>LowestValidUsn</c> keep plain setters, and that is not an
/// oversight: each is ONE fact, so a lone assignment cannot tear anything, and a test that moves
/// one of them is describing the journal's identity rather than publishing a delta. What has to be
/// atomic is the pair a checkpoint is derived from.</para>
///
/// <para><c>Snapshot</c> is <c>init</c> and never written after construction, so it stays out.</para>
/// </summary>
internal sealed class ScriptedUsnReader : IUsnReader
{
    private readonly Lock _sync = new();
    private readonly List<(long SinceUsn, ulong JournalId)> _resumed = [];
    private List<UsnChangeRecord> _changes = [];
    private long _nextUsn = 500;
    private ulong _journalId = 7;
    private long _lowestValidUsn;

    public List<UsnEntry> Snapshot { get; init; } = [];

    /// <summary>
    /// Publishes a delta AND the journal tail it belongs to in ONE critical section — the only way
    /// to move either, which is the point: there is no spelling of "set the records now and the
    /// tail in a moment" for a live worker to land in the middle of. An empty <paramref name="changes"/>
    /// is a legitimate script — it is how "the journal moved on and we changed nothing" is said.
    /// </summary>
    /// <param name="changes">Copied, so a caller that goes on building its own list cannot reach
    /// inside a reader a worker is already reading.</param>
    public void Script(IReadOnlyList<UsnChangeRecord> changes, long nextUsn)
    {
        lock (_sync)
        {
            _changes = [.. changes];
            _nextUsn = nextUsn;
        }
    }

    public ulong JournalId
    {
        get { lock (_sync) { return _journalId; } }
        set { lock (_sync) { _journalId = value; } }
    }

    public long LowestValidUsn
    {
        get { lock (_sync) { return _lowestValidUsn; } }
        set { lock (_sync) { _lowestValidUsn = value; } }
    }

    /// <summary>
    /// Cursors this reader was asked to resume from, oldest first — a COPY, because the worker goes
    /// on appending to the real list while the assertion walks what it was handed.
    /// </summary>
    public IReadOnlyList<(long SinceUsn, ulong JournalId)> Resumed
    {
        get { lock (_sync) { return _resumed.ToList(); } }
    }

    public int ReadChangesCalls
    {
        get { lock (_sync) { return _resumed.Count; } }
    }

    public bool SupportsUsn(string volumeGuid) => true;

    public UsnJournalState GetJournalState(string volumeGuid)
    {
        // One lock for all three, so the state a caller reads is one the test actually wrote and
        // not a mix of two of them.
        lock (_sync)
        {
            return new UsnJournalState(_journalId, FirstUsn: 0, NextUsn: _nextUsn, LowestValidUsn: _lowestValidUsn);
        }
    }

    public void EnsureJournal(string volumeGuid) { }

    public IEnumerable<UsnEntry> ReadFullSnapshot(string volumeGuid, CancellationToken ct) => Snapshot;

    /// <summary>
    /// The same two invalidation rules the real reader applies, so a test can trigger them by
    /// moving the journal instead of by faking the answer.
    /// </summary>
    public UsnChangeResult ReadChanges(string volumeGuid, long sinceUsn, ulong journalId, CancellationToken ct)
    {
        // The whole answer is composed under the lock: the delta a caller is handed and the NextUsn
        // it will checkpoint have to come from the same version of this fixture. Reading them under
        // one lock is only half of it — the writing side has to be one section too, which is why
        // Script exists and the two properties it replaced do not.
        lock (_sync)
        {
            _resumed.Add((sinceUsn, journalId));

            if (journalId != _journalId || sinceUsn < _lowestValidUsn)
            {
                return new UsnChangeResult([], _nextUsn, RequiresFullRescan: true);
            }

            // Only what the caller has not consumed yet, so a second pass on the same cursor is empty.
            var pending = _changes.Where(c => c.Entry.Usn >= sinceUsn).ToList();
            return new UsnChangeResult(pending, _nextUsn, RequiresFullRescan: false);
        }
    }
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

/// <summary>
/// The metadata port over a map the test supplies — and hands straight back, so a test can express
/// "a file appeared on disk" by adding to it.
///
/// <para><b>A caller that mutates the map while a live worker holds this reader MUST pass a
/// concurrent one</b> (<c>UsnSyncWorkerTests.Disk</c> does, and says so). Reading a plain
/// <see cref="Dictionary{TKey,TValue}"/> during a write to it is not merely stale: it can throw or
/// fail to terminate. Every other caller here builds the map once and never touches it again.</para>
/// </summary>
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
    public Task<long> CountEntriesAsync(CancellationToken ct) => Task.FromResult(0L);
    public Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct) => Task.CompletedTask;
    public Task SyncDirectoriesAsync(IReadOnlyCollection<int> directoryIds, CancellationToken ct) => Task.CompletedTask;
    public Task PruneVolumeAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
    public Task UpsertAsync(int fileId, string name, string path, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveAsync(int fileId, CancellationToken ct) => Task.CompletedTask;
    public Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct)
        => Task.FromResult(new PagedResult<int>([], 0, query.Skip, query.Take));
}

/// <summary>
/// The real index with ONE fault injected: <see cref="SyncDirectoriesAsync"/> throws. Everything
/// else reads and writes through to <paramref name="inner"/>, so the failure is a failure of that
/// call and not of a stand-in for the component under test.
///
/// <para>It exists to reach a property no ordinary case can: that a pass writing exclusion flags
/// and pruning the index for the same directories does BOTH or NEITHER. Crash injection would say
/// the same thing and cannot be written deterministically; a throw at the second half is the same
/// window with a repeatable edge, because the transaction the caller opened is the thing being
/// measured and an exception unwinds it exactly as a crash would leave it uncommitted.</para>
/// </summary>
internal sealed class ExplodingDirectorySyncIndex(IFileSearchIndex inner) : IFileSearchIndex
{
    public Task ClearVolumeAsync(int volumeId, CancellationToken ct) => inner.ClearVolumeAsync(volumeId, ct);
    public Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct) => inner.SyncVolumeFromDbAsync(volumeId, ct);
    public Task RebuildAsync(CancellationToken ct) => inner.RebuildAsync(ct);
    public Task<long> CountEntriesAsync(CancellationToken ct) => inner.CountEntriesAsync(ct);
    public Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct) => inner.SyncFilesAsync(fileIds, ct);
    public Task PruneVolumeAsync(int volumeId, CancellationToken ct) => inner.PruneVolumeAsync(volumeId, ct);
    public Task UpsertAsync(int fileId, string name, string path, CancellationToken ct) => inner.UpsertAsync(fileId, name, path, ct);
    public Task RemoveAsync(int fileId, CancellationToken ct) => inner.RemoveAsync(fileId, ct);
    public Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct) => inner.SearchAsync(query, ct);

    public Task SyncDirectoriesAsync(IReadOnlyCollection<int> directoryIds, CancellationToken ct) =>
        throw new InvalidOperationException("the search index refused this directory sync");
}
