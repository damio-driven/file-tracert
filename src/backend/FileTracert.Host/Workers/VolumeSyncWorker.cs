using FileTracert.Business.Volumes;
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
        await sync.SyncAsync(ct);
        _logger.LogDebug("Volume sync cycle completed.");
    }
}
