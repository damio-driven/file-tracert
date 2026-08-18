using FileTracert.Business.Operations;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Operations;

namespace FileTracert.Host.Infrastructure;

/// <summary>
/// The one volume-reconciliation cycle in the application: re-probe the volumes, and if any
/// came back online revaluate the jobs parked on them and wake the queue processor. It has two
/// triggers — the device-arrival push (<see cref="Workers.DeviceWatcherWorker"/>) and the
/// periodic safety poll (<see cref="Workers.VolumeSyncWorker"/>) — and exactly one body (§9).
/// Singleton, because the gate that serializes the two triggers has to be shared.
/// </summary>
public sealed class VolumeSyncCycle(IServiceProvider services, ILogger<VolumeSyncCycle> logger)
{
    /// <summary>Serializes the two triggers: two cycles must never reconcile the same volumes at once.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Runs one cycle, waiting for any cycle already in flight. Waiting rather than skipping is
    /// deliberate: a running cycle may have enumerated the volumes just before the drive that
    /// triggered this call registered itself, so dropping the second cycle would lose exactly the
    /// arrival it exists for. At most two ever queue up (one debounced device burst, one interval
    /// tick) and a cycle is cheap — it reconciles volume rows, it never scans files.
    /// </summary>
    /// <param name="trigger">What asked for the cycle; appears in the logs.</param>
    public async Task RunAsync(string trigger, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await RunCoreAsync(trigger, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunCoreAsync(string trigger, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<VolumeSyncService>();
        var cameOnline = await sync.SyncAsync(ct);
        logger.LogDebug("Volume sync cycle completed ({Trigger}).", trigger);

        if (cameOnline.Count == 0)
        {
            return;
        }

        // FIX #13 — a volume coming back is the event that resurrects the jobs parked on it (§4).
        // The sync has just refreshed FreeBytesLastKnown from the live probe, so the revaluator's
        // hard space re-check runs on the drive's REAL free space, never on the stale estimate.
        int unblocked = await scope.ServiceProvider
            .GetRequiredService<BlockedJobRevaluator>()
            .RevaluateAsync(ct);

        logger.LogInformation(
            "Volume sync ({Trigger}): {Count} volume(s) back online → {Unblocked} job(s) returned to Pending.",
            trigger, cameOnline.Count, unblocked);

        if (unblocked > 0)
        {
            // Wake the processor now instead of leaving the job to the 30 s safety poll.
            scope.ServiceProvider.GetRequiredService<IQueueSignal>().Signal();
        }
    }
}
