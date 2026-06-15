using System.Collections.Concurrent;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;

namespace FileTracert.Business.Scanning;

/// <summary>
/// Singleton, thread-safe implementation of <see cref="IScanStatusTracker"/>.
/// Entries live only while a scan is in flight; <see cref="Complete"/>/<see cref="Fail"/>
/// remove them so <see cref="Snapshot"/> reflects exactly the active scans. Updates to
/// an untracked volume are ignored (a stray report must never resurrect a finished scan).
/// </summary>
public sealed class ScanStatusTracker : IScanStatusTracker
{
    private sealed class Entry
    {
        public string? Label;
        public ScanPhase Phase;
        public long ItemsSeen;
        public long ItemsWritten;
        public string? CurrentRoot;
        public DateTime StartedUtc;
        public DateTime UpdatedUtc;
    }

    private readonly ConcurrentDictionary<int, Entry> _entries = new();

    public void Begin(int volumeId, string? label)
    {
        var now = DateTime.UtcNow;
        _entries[volumeId] = new Entry
        {
            Label = label,
            Phase = ScanPhase.Enumerating,
            StartedUtc = now,
            UpdatedUtc = now,
        };
    }

    public void SetPhase(int volumeId, ScanPhase phase) =>
        Update(volumeId, e => e.Phase = phase);

    public void ReportSeen(int volumeId, long itemsSeen, string? currentRoot = null) =>
        Update(volumeId, e =>
        {
            e.ItemsSeen = itemsSeen;
            if (currentRoot is not null)
            {
                e.CurrentRoot = currentRoot;
            }
        });

    public void ReportWritten(int volumeId, long itemsWritten) =>
        Update(volumeId, e => e.ItemsWritten = itemsWritten);

    public void Complete(int volumeId) => _entries.TryRemove(volumeId, out _);

    public void Fail(int volumeId) => _entries.TryRemove(volumeId, out _);

    public IReadOnlyList<ScanStatusDto> Snapshot()
    {
        // Snapshot under each entry's lock so we never read a half-updated entry.
        return _entries
            .Select(kvp =>
            {
                var e = kvp.Value;
                lock (e)
                {
                    return new ScanStatusDto(
                        kvp.Key, e.Label, e.Phase, e.ItemsSeen, e.ItemsWritten,
                        e.CurrentRoot, e.StartedUtc, e.UpdatedUtc);
                }
            })
            .OrderBy(s => s.VolumeId)
            .ToList();
    }

    private void Update(int volumeId, Action<Entry> mutate)
    {
        if (!_entries.TryGetValue(volumeId, out var entry))
        {
            return;
        }

        lock (entry)
        {
            mutate(entry);
            entry.UpdatedUtc = DateTime.UtcNow;
        }
    }
}
