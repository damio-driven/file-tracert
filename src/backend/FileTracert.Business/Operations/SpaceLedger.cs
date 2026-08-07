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

    private sealed record LedgerRecord(int JobId, int SequenceOrder, long Delta);

    public SpaceLedger(IServiceScopeFactory scopeFactory, ILogger<SpaceLedger> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── ISpaceLedger ─────────────────────────────────────────────────────────

    public async Task<FeasibilityResult> ComputeFeasibilityAsync(
        int targetVolumeId, long freeBytesLastKnown, bool isOnline,
        long requiredBytes, int? excludeJobId, int? sequenceOrder,
        bool includeQueuedLiberations, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return Compute(targetVolumeId, freeBytesLastKnown, isOnline, requiredBytes,
                excludeJobId, sequenceOrder, includeQueuedLiberations);
        }
        finally { _lock.Release(); }
    }

    public async Task ReserveAsync(int jobId, int sequenceOrder, int targetVolumeId,
                                   long requiredBytes, int? sourceVolumeId, long freedBytes,
                                   CancellationToken ct)
    {
        // Write to DB before mutating in-memory: if the DB write fails the in-memory state stays clean.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            db.SpaceLedgerEntries.AddRange(
                BuildReservationEntries(jobId, targetVolumeId, requiredBytes, sourceVolumeId, freedBytes));
            await db.SaveChangesAsync(ct);
        }

        await RegisterReservationInMemoryAsync(
            jobId, sequenceOrder, targetVolumeId, requiredBytes, sourceVolumeId, freedBytes, ct);

        _logger.LogDebug("SpaceLedger: reserved {Bytes} bytes on volume {Vol} for job {Job}.",
            requiredBytes, targetVolumeId, jobId);
    }

    /// <summary>
    /// Builds the (zero, one, or two) <see cref="SpaceLedgerEntry"/> rows for a job's reservation:
    /// a +reservation on the target and a −liberation on the source. The single place that owns the
    /// delta-sign convention — reused by <see cref="ReserveAsync"/> and by the atomic enqueue path,
    /// which stages these into the job's own transaction (fix C3).
    /// </summary>
    public static IReadOnlyList<SpaceLedgerEntry> BuildReservationEntries(
        int jobId, int targetVolumeId, long requiredBytes, int? sourceVolumeId, long freedBytes)
    {
        var entries = new List<SpaceLedgerEntry>(2);
        if (requiredBytes > 0)
            entries.Add(new SpaceLedgerEntry
            {
                JobId = jobId, VolumeId = targetVolumeId,
                DeltaBytes = +requiredBytes, IsActive = true
            });
        if (sourceVolumeId.HasValue && freedBytes > 0)
            entries.Add(new SpaceLedgerEntry
            {
                JobId = jobId, VolumeId = sourceVolumeId.Value,
                DeltaBytes = -freedBytes, IsActive = true
            });
        return entries;
    }

    public async Task RegisterReservationInMemoryAsync(
        int jobId, int sequenceOrder, int targetVolumeId,
        long requiredBytes, int? sourceVolumeId, long freedBytes, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (requiredBytes > 0)
                AddToMemory(targetVolumeId, jobId, sequenceOrder, +requiredBytes);

            if (sourceVolumeId.HasValue && freedBytes > 0)
                AddToMemory(sourceVolumeId.Value, jobId, sequenceOrder, -freedBytes);
        }
        finally { _lock.Release(); }
    }

    public async Task ReleaseAsync(int jobId, CancellationToken ct)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            await DeactivateEntriesAsync(db, jobId, ct);
        }

        await ReleaseInMemoryAsync(jobId, ct);
    }

    public async Task ReleaseInMemoryAsync(int jobId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RemoveFromMemory(jobId);
        }
        finally { _lock.Release(); }

        _logger.LogDebug("SpaceLedger: released entries for job {Job}.", jobId);
    }

    /// <summary>
    /// Deactivates a job's active entries through the CALLER's DbContext, so a terminal state
    /// change and its ledger release commit in the same transaction (finding #5): a crash can
    /// never leave an IsActive reservation on a terminal job. Static for the same reason as
    /// <see cref="BuildReservationEntries"/> — the durable write belongs to the caller's
    /// unit of work, the singleton only mirrors it in memory after the commit.
    /// </summary>
    public static Task DeactivateEntriesAsync(FileTracertDbContext db, int jobId, CancellationToken ct) =>
        db.SpaceLedgerEntries
            .Where(e => e.JobId == jobId && e.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false), ct);

    public async Task RebuildFromDbAsync(CancellationToken ct)
    {
        // SequenceOrder lives on the job, not on the entry — join it back in so the
        // rebuilt mirror keeps the FIFO ordering information.
        List<(int VolumeId, int JobId, int SequenceOrder, long DeltaBytes)> entries;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();

            // Reconciliation (finding #5): a crash between a terminal-state commit and the
            // (formerly separate) release left IsActive entries on terminal jobs. Loading
            // them would under-count free space forever — heal the rows, then load.
            int reconciled = await db.SpaceLedgerEntries
                .Where(e => e.IsActive && JobStates.Terminal.Contains(e.Job.State))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false), ct);
            if (reconciled > 0)
                _logger.LogWarning(
                    "SpaceLedger: deactivated {Count} phantom ledger entries left by terminal jobs.", reconciled);

            entries = (await db.SpaceLedgerEntries
                .Where(e => e.IsActive)
                .Select(e => new { e.VolumeId, e.JobId, e.Job.SequenceOrder, e.DeltaBytes })
                .AsNoTracking()
                .ToListAsync(ct))
                .Select(e => (e.VolumeId, e.JobId, e.SequenceOrder, e.DeltaBytes))
                .ToList();
        }

        await _lock.WaitAsync(ct);
        try
        {
            _state.Clear();
            _jobVolumeMap.Clear();
            foreach (var e in entries)
                AddToMemory(e.VolumeId, e.JobId, e.SequenceOrder, e.DeltaBytes);
        }
        finally { _lock.Release(); }

        _logger.LogInformation("SpaceLedger: rebuilt from DB — {Count} active entries.", entries.Count);
    }

    // ── private helpers (always called within _lock) ──────────────────────────

    private FeasibilityResult Compute(int targetVolumeId, long freeBytesLastKnown,
                                      bool isOnline, long requiredBytes,
                                      int? excludeJobId, int? sequenceOrder,
                                      bool includeQueuedLiberations)
    {
        long netDelta = 0;
        long reserved = 0;

        if (_state.TryGetValue(targetVolumeId, out var entries))
        {
            foreach (var e in entries)
            {
                // FIFO semantics (§4 of the brief): the evaluated job sees only the effect
                // of jobs ahead of it in the queue, and never its own reservation — counting
                // that would demand ~2× the space and wrongly block a job that fits.
                if (e.JobId == excludeJobId) continue;
                if (sequenceOrder is not null && e.SequenceOrder > sequenceOrder.Value) continue;
                // HARD view: an active negative delta is a liberation not yet materialized
                // (entries are released when the freeing job completes) — planning may credit
                // it, an execution re-check must not (never copy on a promise).
                if (!includeQueuedLiberations && e.Delta < 0) continue;

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

    private void AddToMemory(int volumeId, int jobId, int sequenceOrder, long delta)
    {
        if (!_state.TryGetValue(volumeId, out var list))
            _state[volumeId] = list = [];
        list.Add(new LedgerRecord(jobId, sequenceOrder, delta));

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
