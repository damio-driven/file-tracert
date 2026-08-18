using System.Collections.Concurrent;
using FileTracert.Business.Realtime;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;

namespace FileTracert.Business.Scanning;

/// <summary>
/// Singleton, thread-safe implementation of <see cref="IScanStatusTracker"/>.
/// Entries live only while a scan is in flight; <see cref="Complete"/>/<see cref="Fail"/>
/// remove them so <see cref="Snapshot"/> reflects exactly the active scans. Updates to
/// an untracked volume are ignored (a stray report must never resurrect a finished scan).
///
/// It is also the emission point of <c>ScanProgress</c> (§7). A scan reports items seen several
/// thousand times a second, so the counter pushes are throttled per volume to
/// <see cref="PushInterval"/>; the events that carry MEANING rather than a number — the start,
/// every phase change, and the terminal Done/Failed — are pushed immediately and reset the
/// window, so the client never sits on a stale phase waiting for a tick.
/// </summary>
public sealed class ScanStatusTracker : IScanStatusTracker
{
    /// <summary>Minimum gap between two counter pushes for the same volume.</summary>
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(500);

    private sealed class Entry
    {
        public string? Label;
        public ScanPhase Phase;
        public long ItemsSeen;
        public long ItemsWritten;
        public string? CurrentRoot;
        public DateTime StartedUtc;
        public DateTime UpdatedUtc;
        public long LastPushTimestamp;
    }

    private readonly ConcurrentDictionary<int, Entry> _entries = new();
    private readonly RealtimeEvents _realtime;
    private readonly TimeProvider _timeProvider;

    public ScanStatusTracker(RealtimeEvents realtime, TimeProvider timeProvider)
    {
        _realtime = realtime;
        _timeProvider = timeProvider;
    }

    public void Begin(int volumeId, string? label)
    {
        var now = DateTime.UtcNow;
        var entry = new Entry
        {
            Label = label,
            Phase = ScanPhase.Enumerating,
            StartedUtc = now,
            UpdatedUtc = now,
            LastPushTimestamp = _timeProvider.GetTimestamp(),
        };
        _entries[volumeId] = entry;
        Push(volumeId, entry);
    }

    public void SetPhase(int volumeId, ScanPhase phase) =>
        Update(volumeId, e => e.Phase = phase, always: true);

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

    public void Complete(int volumeId) => Finish(volumeId, ScanPhase.Done);

    public void Fail(int volumeId) => Finish(volumeId, ScanPhase.Failed);

    /// <summary>
    /// Drops the entry and pushes one last frame carrying the terminal phase. The removal keeps
    /// <see cref="Snapshot"/> honest (nothing is scanning any more); the push is what tells a
    /// client that the scan ENDED — otherwise the progress simply stops arriving and the UI
    /// cannot tell "finished" from "the connection died".
    /// </summary>
    private void Finish(int volumeId, ScanPhase phase)
    {
        if (!_entries.TryRemove(volumeId, out var entry))
        {
            return;
        }

        lock (entry)
        {
            entry.Phase = phase;
            entry.UpdatedUtc = DateTime.UtcNow;
        }

        Push(volumeId, entry);
    }

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

    /// <param name="always">
    /// True for a change that is not merely a bigger number (a phase change): it bypasses the
    /// throttle. False for counter reports, which are throttled.
    /// </param>
    private void Update(int volumeId, Action<Entry> mutate, bool always = false)
    {
        if (!_entries.TryGetValue(volumeId, out var entry))
        {
            return;
        }

        bool push;
        lock (entry)
        {
            mutate(entry);
            entry.UpdatedUtc = DateTime.UtcNow;

            push = always || _timeProvider.GetElapsedTime(entry.LastPushTimestamp) >= PushInterval;
            if (push)
            {
                entry.LastPushTimestamp = _timeProvider.GetTimestamp();
            }
        }

        if (push)
        {
            Push(volumeId, entry);
        }
    }

    /// <summary>
    /// Fire-and-forget on purpose: <see cref="IScanStatusTracker"/> is a synchronous, in-memory
    /// tracker called from the middle of the scan pipeline, and making a progress report await a
    /// network send would put the transport on the scan critical path.
    /// <c>RealtimeEvents</c> swallows and logs every failure, so no task ever faults unobserved.
    /// The cost is that two frames may reach the client out of order; each carries the full
    /// counters and the terminal frame is what the UI keys off, so a swap is harmless.
    /// </summary>
    private void Push(int volumeId, Entry entry)
    {
        ScanStatusDto dto;
        lock (entry)
        {
            dto = new ScanStatusDto(
                volumeId, entry.Label, entry.Phase, entry.ItemsSeen, entry.ItemsWritten,
                entry.CurrentRoot, entry.StartedUtc, entry.UpdatedUtc);
        }

        _ = _realtime.ScanProgressAsync(dto);
    }
}
