using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// How much room a volume has, and whether the number was read from the device just now.
/// <c>IsLive == false</c> means the volume did not answer and the figure is the last one the
/// catalog stored — usable for planning, never for committing to a copy (§4).
/// </summary>
public readonly record struct VolumeFreeSpace(long FreeBytes, bool IsLive);

/// <summary>
/// The verdict of the hard (execution-time) space check for one job.
/// <see cref="Ok"/> false always carries a <see cref="Reason"/> the queue can park the job on —
/// a recoverable Blocked, never a Failed (§4).
/// </summary>
/// <param name="Feasibility">
/// Always present, so the UI can explain the block with the same numbers the engine used.
/// </param>
/// <param name="Space">
/// The figure the verdict was taken on. <c>IsLive</c> false also covers the job that needed no
/// check at all: the caller must not store a measurement that was never made.
/// </param>
public sealed record HardSpaceVerdict(
    bool Ok,
    JobBlockReason Reason,
    string Message,
    FeasibilityResult Feasibility,
    VolumeFreeSpace Space);

/// <summary>
/// The single place that answers "does this fit?" with fresh numbers.
///
/// Two views, one implementation (§4):
///  - <see cref="PlanAsync"/> — the PLANNING view used by preview and enqueue: promised
///    liberations of preceding jobs count, and a volume that cannot be reached falls back to its
///    last-known figure rather than refusing the job (§4: never refuse at enqueue).
///  - <see cref="EvaluateHardAsync"/> — the HARD view used by the engine right before it copies
///    and by the revaluation that decides a parked job may run: promised liberations do not
///    count, and a volume that cannot be reached blocks the job. This is §4's "mai copiare sulla
///    fiducia di una stima": the number compared here comes from the disk, not from the row the
///    last volume sync wrote, because between that sync and this instant another process can have
///    written tens of gigabytes.
///
/// Scoped, and memoizes per scope on purpose: a revaluation pass judges every candidate against
/// ONE snapshot of the drive (nothing has been copied in between, so a second syscall would only
/// invite two jobs of the same pass to see different worlds), and a job list with fifty blocked
/// rows costs one probe per volume instead of fifty.
/// </summary>
public sealed class SpaceCheck
{
    /// <summary>
    /// Upper bound accepted from <c>AppSettings.SpaceMarginPercent</c>. §4 asks for 2–5%; a
    /// cushion worth more than half the operation has stopped being a cushion, and a typo
    /// (300 instead of 3) must not park the whole queue in silence.
    /// </summary>
    private const int MaxMarginPercent = 50;

    private readonly FileTracertDbContext _db;
    private readonly ISpaceLedger _ledger;
    private readonly IVolumeProbe _probe;
    private readonly ILogger<SpaceCheck> _logger;

    private readonly Dictionary<int, VolumeFreeSpace> _freeSpaceByVolume = [];
    private int? _marginPercent;

    public SpaceCheck(
        FileTracertDbContext db,
        ISpaceLedger ledger,
        IVolumeProbe probe,
        ILogger<SpaceCheck> logger)
    {
        _db = db;
        _ledger = ledger;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>
    /// Free bytes on the volume, read from the device when it answers and from
    /// <see cref="Volume.FreeBytesLastKnown"/> when it does not. Memoized for the scope.
    ///
    /// A volume the catalog already knows is disconnected is NOT probed: the answer is known,
    /// and asking anyway would cost a syscall that can stall on a half-removed device and a
    /// Warning per call — on a read path like the queue list, one per screen refresh, for a fact
    /// nobody disputes.
    /// </summary>
    public VolumeFreeSpace ReadFreeSpace(Volume volume)
    {
        if (_freeSpaceByVolume.TryGetValue(volume.Id, out var cached))
            return cached;

        var probed = volume.IsOnline ? _probe.TryGetFreeBytes(volume.VolumeGuid) : null;
        var space = probed is { } live
            ? new VolumeFreeSpace(live, IsLive: true)
            : new VolumeFreeSpace(volume.FreeBytesLastKnown, IsLive: false);

        if (probed is null && volume.IsOnline)
        {
            // Resilience, not silence (§9): the port already logged the Win32 cause; this line
            // says what the queue is going to do about it. Only for a volume that is SUPPOSED to
            // be there — a known-offline one is not news.
            _logger.LogWarning(
                "Volume {Id} ({Guid}) is flagged online but did not answer the free-space probe — " +
                "falling back to the last known {Bytes} bytes, which is planning-only.",
                volume.Id, volume.VolumeGuid, volume.FreeBytesLastKnown);
        }

        _freeSpaceByVolume[volume.Id] = space;
        return space;
    }

    /// <summary>
    /// A FRESH reading: drops the memo for this volume and asks again. For the one moment where
    /// the memoized snapshot is knowingly out of date — after a job has physically moved bytes —
    /// and never on the decision path, which must judge every candidate of a pass against one
    /// and the same picture of the drive.
    /// </summary>
    public VolumeFreeSpace Measure(Volume volume)
    {
        _freeSpaceByVolume.Remove(volume.Id);
        return ReadFreeSpace(volume);
    }

    /// <summary>
    /// The configured safety margin, read once per scope and clamped to something a queue can
    /// survive. Zero when the settings row is missing — an anomaly must not park every job.
    /// </summary>
    public async Task<int> MarginPercentAsync(CancellationToken ct)
    {
        if (_marginPercent is { } cached) return cached;

        int configured = await _db.AppSettings.AsNoTracking()
            .Select(s => s.SpaceMarginPercent)
            .FirstOrDefaultAsync(ct);

        int clamped = Math.Clamp(configured, 0, MaxMarginPercent);
        if (clamped != configured)
        {
            _logger.LogWarning(
                "AppSettings.SpaceMarginPercent is {Configured}%, outside the supported 0–{Max}% " +
                "range; using {Clamped}% instead.", configured, MaxMarginPercent, clamped);
        }

        _marginPercent = clamped;
        return clamped;
    }

    /// <summary>
    /// The cushion in bytes for a demand of <paramref name="requiredBytes"/>.
    ///
    /// A percentage OF THE DEMAND, not of the free space, and the difference is the whole point:
    /// what the margin covers is the gap between the sum of the file sizes and what actually
    /// lands on the target — cluster slack, metadata, streams — plus whatever else writes to the
    /// drive while we copy. All three grow with the size of the operation, not with the size of
    /// the drive. A percentage of the free space would demand 60 GB of headroom to move a
    /// kilobyte onto a 2 TB volume, and next to nothing when the volume is nearly full, which is
    /// exactly backwards.
    /// </summary>
    public static long MarginBytesFor(long requiredBytes, int marginPercent) =>
        requiredBytes <= 0 || marginPercent <= 0
            ? 0
            : (long)((decimal)requiredBytes * marginPercent / 100m);

    /// <summary>
    /// Planning feasibility for a prospective or queued job on <paramref name="volume"/>.
    /// Never blocks anything by itself: the caller decides what an infeasible answer means.
    /// The margin applies here too — an enqueue that promised room the engine would then refuse
    /// would only move the disappointment one step later — but §4 still holds: an infeasible
    /// answer means Blocked, never a refusal.
    /// </summary>
    public async Task<FeasibilityResult> PlanAsync(
        Volume volume, long requiredBytes, int? excludeJobId, int? sequenceOrder, CancellationToken ct)
    {
        var space = ReadFreeSpace(volume);
        long margin = MarginBytesFor(requiredBytes, await MarginPercentAsync(ct));
        return await _ledger.ComputeFeasibilityAsync(
            volume.Id, space.FreeBytes, space.IsLive, requiredBytes, margin,
            excludeJobId, sequenceOrder, includeQueuedLiberations: true, ct);
    }

    /// <summary>
    /// The hard re-check for a job that is about to run (or that a revaluation wants to release).
    /// A job that needs no space on a target volume passes without touching the device.
    /// </summary>
    public async Task<HardSpaceVerdict> EvaluateHardAsync(OperationJob job, CancellationToken ct)
    {
        if (job.IsIntraVolume || job.RequiredBytesTarget <= 0 || job.TargetVolume is null)
            return NothingToCheck;

        var volume = job.TargetVolume;
        var space = ReadFreeSpace(volume);

        long margin = MarginBytesFor(job.RequiredBytesTarget, await MarginPercentAsync(ct));
        var feasibility = await _ledger.ComputeFeasibilityAsync(
            volume.Id, space.FreeBytes, space.IsLive, job.RequiredBytesTarget, margin,
            excludeJobId: job.Id, sequenceOrder: job.SequenceOrder,
            includeQueuedLiberations: false, ct);

        if (!space.IsLive)
        {
            // The offline gate normally answers first; this is the volume the catalog believes is
            // connected and that the device layer cannot measure anyway. It is parked on the same
            // reason and in the same language as the gate — the Coda labels that reason "volume
            // di destinazione offline", so the sentence beside it must not contradict the label.
            return new HardSpaceVerdict(
                Ok: false,
                JobBlockReason.TargetVolumeOffline,
                $"Il volume di destinazione {VolumeOfflineGate.Name(volume)} non risponde alla " +
                "lettura dello spazio libero: l'operazione resta in coda invece di copiare su una " +
                "stima, e riparte da sola quando il volume risponde.",
                feasibility,
                space);
        }

        if (!feasibility.Feasible)
        {
            return new HardSpaceVerdict(
                Ok: false,
                JobBlockReason.InsufficientSpace,
                $"Insufficient space: {feasibility.DeficitBytes} bytes short on volume {volume.Id} " +
                $"(required {job.RequiredBytesTarget}, safety margin {margin}).",
                feasibility,
                space);
        }

        return new HardSpaceVerdict(Ok: true, JobBlockReason.None, string.Empty, feasibility, space);
    }

    /// <summary>
    /// A job with nothing to reserve: feasible by construction, no device touched — hence a space
    /// that is explicitly NOT live, so no caller mistakes it for a reading.
    /// </summary>
    private static readonly HardSpaceVerdict NothingToCheck = new(
        Ok: true, JobBlockReason.None, string.Empty,
        new FeasibilityResult(0, 0, long.MaxValue, 0, EstimateIsLive: false, null, Feasible: true),
        new VolumeFreeSpace(0, IsLive: false));
}
