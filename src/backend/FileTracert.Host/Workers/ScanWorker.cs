using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
using FileTracert.Data;
using FileTracert.Host.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileTracert.Host.Workers;

/// <summary>
/// Runs full scans <em>one volume at a time</em> (no parallel disk thrashing).
/// Each cycle it picks up volumes that are online, have active watched roots and
/// were never fully scanned, plus any explicit <see cref="IScanScheduler"/>
/// requests. A per-volume error is logged and does not abort the worker.
/// </summary>
public sealed class ScanWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IScanScheduler _scheduler;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<ScanWorker> _logger;

    public ScanWorker(
        IServiceProvider services,
        IScanScheduler scheduler,
        IOptions<FileTracertOptions> options,
        ILogger<ScanWorker> logger)
    {
        _services = services;
        _scheduler = scheduler;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.ScanPollIntervalSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var volumeIds = await CollectPendingAsync(stoppingToken);
                foreach (var id in volumeIds)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await ScanOneAsync(id, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan cycle failed; will retry next interval.");
            }

            if (!await WaitForNextAsync(stoppingToken))
            {
                break;
            }
        }
    }

    /// <summary>Never-scanned online volumes plus explicit requests, de-duplicated, order-stable.</summary>
    private async Task<List<int>> CollectPendingAsync(CancellationToken ct)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();

        // Drain explicit scan requests first so a manual trigger jumps the queue.
        while (_scheduler.Requests.TryRead(out var requested))
        {
            if (seen.Add(requested))
            {
                ids.Add(requested);
            }
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        var pending = await db.Volumes
            .Where(v => v.IsOnline
                && v.IsCatalogable
                && v.LastFullScanUtc == null
                && v.WatchedRoots.Any(r => r.IsActive))
            .Select(v => v.Id)
            .ToListAsync(ct);

        foreach (var id in pending)
        {
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task ScanOneAsync(int volumeId, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<ScanService>();
            _logger.LogInformation("Starting full scan of volume {VolumeId}.", volumeId);
            await scanner.ScanVolumeAsync(volumeId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad volume (offline mid-scan, no mount point…) must not kill the worker,
            // but the failure must be visible: full log + a user-facing notification.
            _logger.LogError(ex, "Full scan of volume {VolumeId} failed.", volumeId);
            await PublishScanFailureAsync(volumeId, ex, ct);
        }
    }

    /// <summary>Records a user-visible notification for a failed volume scan. Best-effort.</summary>
    private async Task PublishScanFailureAsync(int volumeId, Exception ex, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var label = await scope.ServiceProvider.GetRequiredService<FileTracertDbContext>()
                .Volumes.Where(v => v.Id == volumeId)
                .Select(v => v.Label ?? v.VolumeGuid)
                .FirstOrDefaultAsync(ct) ?? $"#{volumeId}";

            await scope.ServiceProvider.GetRequiredService<INotificationPublisher>().PublishAsync(
                NotificationSeverity.Error,
                "Scan",
                $"Scansione fallita per «{label}»",
                ex.ToString(),
                volumeId,
                ct);
        }
        catch (Exception notifyEx)
        {
            // Notifying about a failure must not itself crash the worker.
            _logger.LogError(notifyEx, "Failed to record scan-failure notification for volume {VolumeId}.", volumeId);
        }
    }

    /// <summary>Waits for either the poll interval or an explicit scan request.</summary>
    private async Task<bool> WaitForNextAsync(CancellationToken ct)
    {
        try
        {
            var delay = Task.Delay(_pollInterval, ct);
            var signal = _scheduler.Requests.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(delay, signal);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
