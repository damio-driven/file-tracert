using FileTracert.Business.Operations;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Operations;
using FileTracert.Host.Configuration;
using Microsoft.Extensions.Options;

namespace FileTracert.Host.Workers;

/// <summary>
/// Reconciles the catalog's volumes with what is physically present: runs once at
/// startup and then on a fixed interval. Lightweight — it never scans files. A
/// failure in one cycle is logged and the loop continues.
/// </summary>
public sealed class VolumeSyncWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeSpan _interval;
    private readonly ILogger<VolumeSyncWorker> _logger;

    public VolumeSyncWorker(
        IServiceProvider services,
        IOptions<FileTracertOptions> options,
        ILogger<VolumeSyncWorker> logger)
    {
        _services = services;
        _interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.VolumeSyncIntervalSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Volume sync cycle failed; will retry next interval.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<VolumeSyncService>();
        var cameOnline = await sync.SyncAsync(ct);
        _logger.LogDebug("Volume sync cycle completed.");

        if (cameOnline.Count == 0)
        {
            return;
        }

        // FIX #13 — a volume coming back is the event that resurrects the jobs parked on it (§4).
        // The sync has just refreshed FreeBytesLastKnown from the live probe, so the revaluator's
        // hard space re-check runs on the drive's REAL free space, never on the stale estimate.
        // Polling today; step 10 replaces this trigger with the device-watcher push.
        int unblocked = await scope.ServiceProvider
            .GetRequiredService<BlockedJobRevaluator>()
            .RevaluateAsync(ct);

        _logger.LogInformation(
            "Volume sync: {Count} volume(s) back online → {Unblocked} job(s) returned to Pending.",
            cameOnline.Count, unblocked);

        if (unblocked > 0)
        {
            // Wake the processor now instead of leaving the job to the 30 s safety poll.
            scope.ServiceProvider.GetRequiredService<IQueueSignal>().Signal();
        }
    }
}
