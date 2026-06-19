using FileTracert.Contracts.Operations;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Thread-safe singleton implementing <see cref="ISpaceLedger"/>.
/// Keeps an in-memory mirror of active <c>SpaceLedgerEntries</c> for fast reads
/// (preview / feasibility checks arrive on API threads concurrently with the processor).
/// Mutations (Reserve, Release) persist to DB first, then update in-memory.
/// The mirror is rebuilt from DB on startup via <see cref="RebuildFromDbAsync"/>.
/// </summary>
public sealed class SpaceLedger : ISpaceLedger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SpaceLedger> _logger;

    // volume ID → active entries. Protected by _lock.
    private readonly Dictionary<int, List<LedgerRecord>> _state = new();
    // job ID → volume IDs it has entries on (for O(jobs) release instead of O(entries)). Protected by _lock.
    private readonly Dictionary<int, HashSet<int>> _jobVolumeMap = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private sealed record LedgerRecord(int JobId, long Delta);

    public SpaceLedger(IServiceScopeFactory scopeFactory, ILogger<SpaceLedger> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── ISpaceLedger ─────────────────────────────────────────────────────────

    public async Task<FeasibilityResult> ComputeFeasibilityAsync(
        int targetVolumeId, long freeBytesLastKnown, bool isOnline,
        long requiredBytes, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return Compute(targetVolumeId, freeBytesLastKnown, isOnline, requiredBytes);
        }
        finally { _lock.Release(); }
    }

    public async Task ReserveAsync(int jobId, int targetVolumeId, long requiredBytes,
                                   int? sourceVolumeId, long freedBytes, CancellationToken ct)
    {
        // Write to DB before mutating in-memory: if the DB write fails the in-memory state stays clean.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();

            if (requiredBytes > 0)
                db.SpaceLedgerEntries.Add(new SpaceLedgerEntry
                {
                    JobId = jobId, VolumeId = targetVolumeId,
                    DeltaBytes = +requiredBytes, IsActive = true
                });

            if (sourceVolumeId.HasValue && freedBytes > 0)
                db.SpaceLedgerEntries.Add(new SpaceLedgerEntry
                {
                    JobId = jobId, VolumeId = sourceVolumeId.Value,
                    DeltaBytes = -freedBytes, IsActive = true
                });

            await db.SaveChangesAsync(ct);
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (requiredBytes > 0)
                AddToMemory(targetVolumeId, jobId, +requiredBytes);

            if (sourceVolumeId.HasValue && freedBytes > 0)
                AddToMemory(sourceVolumeId.Value, jobId, -freedBytes);
        }
        finally { _lock.Release(); }

        _logger.LogDebug("SpaceLedger: reserved {Bytes} bytes on volume {Vol} for job {Job}.",
            requiredBytes, targetVolumeId, jobId);
    }

    public async Task ReleaseAsync(int jobId, CancellationToken ct)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            await db.SpaceLedgerEntries
                .Where(e => e.JobId == jobId && e.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false), ct);
        }

        await _lock.WaitAsync(ct);
        try
        {
            RemoveFromMemory(jobId);
        }
        finally { _lock.Release(); }

        _logger.LogDebug("SpaceLedger: released entries for job {Job}.", jobId);
    }

    public async Task RebuildFromDbAsync(CancellationToken ct)
    {
        List<SpaceLedgerEntry> entries;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            entries = await db.SpaceLedgerEntries
                .Where(e => e.IsActive)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        await _lock.WaitAsync(ct);
        try
        {
            _state.Clear();
            _jobVolumeMap.Clear();
            foreach (var e in entries)
                AddToMemory(e.VolumeId, e.JobId, e.DeltaBytes);
        }
        finally { _lock.Release(); }

        _logger.LogInformation("SpaceLedger: rebuilt from DB — {Count} active entries.", entries.Count);
    }

    // ── private helpers (always called within _lock) ──────────────────────────

    private FeasibilityResult Compute(int targetVolumeId, long freeBytesLastKnown,
                                      bool isOnline, long requiredBytes)
    {
        long netDelta = 0;
        long reserved = 0;

        if (_state.TryGetValue(targetVolumeId, out var entries))
        {
            foreach (var e in entries)
            {
                netDelta += e.Delta;
                if (e.Delta > 0) reserved += e.Delta;
            }
        }

        // available = free − Σ(deltas)
        // positive deltas reduce available; negative deltas (liberations on target) increase it
        long available = Math.Max(0, freeBytesLastKnown - netDelta);
        long deficit = Math.Max(0, requiredBytes - available);
        bool feasible = deficit == 0;

        return new FeasibilityResult(
            RequiredBytes: requiredBytes,
            ReservedBytes: reserved,
            AvailableEstimateBytes: available,
            DeficitBytes: deficit,
            EstimateIsLive: isOnline,
            BlockingVolumeId: feasible ? null : targetVolumeId,
            Feasible: feasible);
    }

    private void AddToMemory(int volumeId, int jobId, long delta)
    {
        if (!_state.TryGetValue(volumeId, out var list))
            _state[volumeId] = list = [];
        list.Add(new LedgerRecord(jobId, delta));

        if (!_jobVolumeMap.TryGetValue(jobId, out var vols))
            _jobVolumeMap[jobId] = vols = [];
        vols.Add(volumeId);
    }

    private void RemoveFromMemory(int jobId)
    {
        if (!_jobVolumeMap.TryGetValue(jobId, out var vols)) return;

        foreach (var volId in vols)
        {
            if (_state.TryGetValue(volId, out var list))
                list.RemoveAll(e => e.JobId == jobId);
        }

        _jobVolumeMap.Remove(jobId);
    }
}
