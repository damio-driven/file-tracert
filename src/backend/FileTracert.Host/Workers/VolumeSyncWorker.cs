using FileTracert.Host.Configuration;
using FileTracert.Host.Infrastructure;
using Microsoft.Extensions.Options;

namespace FileTracert.Host.Workers;

/// <summary>
/// Periodic safety net for volume reconciliation: runs <see cref="VolumeSyncCycle"/> once at
/// startup and then on a fixed interval. The primary trigger is the device-arrival push
/// (<see cref="DeviceWatcherWorker"/>); this loop stays so a watcher that failed to register,
/// or a notification the OS never delivered, still cannot leave a mounted drive unnoticed.
/// A failure in one cycle is logged and the loop continues.
/// </summary>
public sealed class VolumeSyncWorker : BackgroundService
{
    private readonly VolumeSyncCycle _cycle;
    private readonly TimeSpan _interval;
    private readonly ILogger<VolumeSyncWorker> _logger;

    public VolumeSyncWorker(
        VolumeSyncCycle cycle,
        IOptions<FileTracertOptions> options,
        ILogger<VolumeSyncWorker> logger)
    {
        _cycle = cycle;
        _interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.VolumeSyncIntervalSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _cycle.RunAsync("interval", stoppingToken);
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
}
