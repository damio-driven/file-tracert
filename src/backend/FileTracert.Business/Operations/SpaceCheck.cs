using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Data.Entities;
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
public sealed record HardSpaceVerdict(
    bool Ok,
    JobBlockReason Reason,
    string Message,
    FeasibilityResult Feasibility);

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
    private readonly ISpaceLedger _ledger;
    private readonly IVolumeProbe _probe;
    private readonly ILogger<SpaceCheck> _logger;

    private readonly Dictionary<int, VolumeFreeSpace> _freeSpaceByVolume = [];

    public SpaceCheck(
        ISpaceLedger ledger,
        IVolumeProbe probe,
        ILogger<SpaceCheck> logger)
    {
        _ledger = ledger;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>
    /// Free bytes on the volume, read from the device when it answers and from
    /// <see cref="Volume.FreeBytesLastKnown"/> when it does not. Memoized for the scope.
    /// </summary>
    public VolumeFreeSpace ReadFreeSpace(Volume volume)
    {
        if (_freeSpaceByVolume.TryGetValue(volume.Id, out var cached))
            return cached;

        var probed = _probe.TryGetFreeBytes(volume.VolumeGuid);
        var space = probed is { } live
            ? new VolumeFreeSpace(live, IsLive: true)
            : new VolumeFreeSpace(volume.FreeBytesLastKnown, IsLive: false);

        if (probed is null)
        {
            // Resilience, not silence (§9): the port already logged the Win32 cause; this line
            // says what the queue is going to do about it.
            _logger.LogWarning(
                "Volume {Id} ({Guid}) did not answer the free-space probe — falling back to the " +
                "last known {Bytes} bytes, which is planning-only.",
                volume.Id, volume.VolumeGuid, volume.FreeBytesLastKnown);
        }

        _freeSpaceByVolume[volume.Id] = space;
        return space;
    }

    /// <summary>
    /// Planning feasibility for a prospective or queued job on <paramref name="volume"/>.
    /// Never blocks anything by itself: the caller decides what an infeasible answer means.
    /// </summary>
    public Task<FeasibilityResult> PlanAsync(
        Volume volume, long requiredBytes, int? excludeJobId, int? sequenceOrder, CancellationToken ct)
    {
        var space = ReadFreeSpace(volume);
        return _ledger.ComputeFeasibilityAsync(
            volume.Id, space.FreeBytes, space.IsLive, requiredBytes,
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

        var feasibility = await _ledger.ComputeFeasibilityAsync(
            volume.Id, space.FreeBytes, space.IsLive, job.RequiredBytesTarget,
            excludeJobId: job.Id, sequenceOrder: job.SequenceOrder,
            includeQueuedLiberations: false, ct);

        if (!space.IsLive)
        {
            // The offline gate normally answers first; a volume that is flagged online yet cannot
            // be measured is the same situation seen one layer down, and it is recoverable.
            return new HardSpaceVerdict(
                Ok: false,
                JobBlockReason.TargetVolumeOffline,
                $"Target volume {volume.Id} did not answer the free-space probe: the operation " +
                $"waits instead of copying on an estimate.",
                feasibility);
        }

        if (!feasibility.Feasible)
        {
            return new HardSpaceVerdict(
                Ok: false,
                JobBlockReason.InsufficientSpace,
                $"Insufficient space: {feasibility.DeficitBytes} bytes short on volume {volume.Id}.",
                feasibility);
        }

        return new HardSpaceVerdict(Ok: true, JobBlockReason.None, string.Empty, feasibility);
    }

    /// <summary>A job with nothing to reserve: feasible by construction, no device touched.</summary>
    private static readonly HardSpaceVerdict NothingToCheck = new(
        Ok: true, JobBlockReason.None, string.Empty,
        new FeasibilityResult(0, 0, long.MaxValue, 0, EstimateIsLive: true, null, Feasible: true));
}
