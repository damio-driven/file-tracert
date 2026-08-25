using FileTracert.Business.Realtime;
using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
using FileTracert.Data;
using FileTracert.Host.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileTracert.Host.Workers;

/// <summary>
/// The incremental half of §1.2, and the reason the USN journal is worth having: instead of
/// walking the whole MFT again, each cycle asks every eligible NTFS volume what changed since its
/// checkpoint and applies just that (<see cref="UsnDeltaApplier"/>).
///
/// <para><b>It never replaces the full scan, it shortens the road to it.</b> A volume that has
/// never been scanned, one indexed by enumeration, one whose journal cursor died — all of them
/// fall through to <see cref="ScanWorker"/> exactly as before. This worker only ever takes a
/// volume that already has a valid cursor, and the moment that cursor stops being valid it hands
/// the volume back, loudly (§9: log + a user-visible notification), never in silence.</para>
///
/// <para>One volume at a time, like <see cref="ScanWorker"/>: the passes share SQLite's single
/// write lock, and a delta is short enough that serialising them costs nothing.</para>
/// </summary>
public sealed class UsnSyncWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IScanScheduler _scheduler;
    private readonly TimeSpan _interval;
    private readonly ILogger<UsnSyncWorker> _logger;

    public UsnSyncWorker(
        IServiceProvider services,
        IScanScheduler scheduler,
        IOptions<FileTracertOptions> options,
        ILogger<UsnSyncWorker> logger)
    {
        _services = services;
        _scheduler = scheduler;
        _interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.UsnSyncIntervalSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var volumeId in await CollectEligibleAsync(stoppingToken))
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await SyncOneAsync(volumeId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "USN sync cycle failed; will retry next interval.");
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

    /// <summary>
    /// The volumes that can take the short road, filtered in SQL so a machine full of volumes does
    /// not pay a journal handle per cycle for each one. <see cref="UsnDeltaApplier"/> re-checks
    /// every one of these (and the ones only it can see, like the mount) — this is a cheap
    /// pre-filter, not the authority.
    /// </summary>
    private async Task<List<int>> CollectEligibleAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();

        return await db.Volumes
            .Where(v => v.IsOnline
                && v.IsCatalogable
                && v.ScanEngine == VolumeScanEngine.UsnJournal
                && v.LastFullScanUtc != null
                && v.LastUsn != null
                && v.UsnJournalId != null
                && v.WatchedRoots.Any(r => r.IsActive))
            .Select(v => v.Id)
            .ToListAsync(ct);
    }

    private async Task SyncOneAsync(int volumeId, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var applier = scope.ServiceProvider.GetRequiredService<UsnDeltaApplier>();
            var result = await applier.SyncVolumeAsync(volumeId, ct);

            switch (result.Status)
            {
                case UsnSyncStatus.Applied:
                    // The catalog moved without anyone asking, so the open screens have to hear
                    // about it: since step 10c they do not poll, they are patched by the hub.
                    await scope.ServiceProvider.GetRequiredService<RealtimeEvents>()
                        .CatalogChangedAsync(volumeId);
                    break;

                case UsnSyncStatus.RescanRequired:
                    await RequestFullScanAsync(scope.ServiceProvider, volumeId, result.Reason, ct);
                    break;

                case UsnSyncStatus.NotEligible:
                    // The pre-filter and the applier disagree only on things the applier can see
                    // and SQL cannot (the volume is not mounted right now). Nothing to fix.
                    _logger.LogDebug(
                        "Volume {VolumeId} skipped by the USN sync: {Reason}.", volumeId, result.Reason);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad volume must not kill the worker, and must not be silent (§9). The volume
            // keeps its cursor, so the next cycle simply tries the same delta again.
            _logger.LogError(ex, "Incremental USN sync of volume {VolumeId} failed.", volumeId);
        }
    }

    /// <summary>
    /// Hands the volume back to the full scan and says so out loud. The applier has already
    /// dropped the cursor, so this happens once per invalidation and not once per cycle.
    /// </summary>
    private async Task RequestFullScanAsync(
        IServiceProvider scoped, int volumeId, string reason, CancellationToken ct)
    {
        _logger.LogWarning(
            "Volume {VolumeId} needs a full scan: {Reason}. The incremental cursor has been dropped.",
            volumeId, reason);

        var label = await scoped.GetRequiredService<FileTracertDbContext>()
            .Volumes.Where(v => v.Id == volumeId)
            .Select(v => v.Label ?? v.VolumeGuid)
            .FirstOrDefaultAsync(ct) ?? $"#{volumeId}";

        await scoped.GetRequiredService<INotificationPublisher>().PublishAsync(
            NotificationSeverity.Warning,
            "Scan",
            $"Rilettura completa necessaria per «{label}»",
            "Il giornale delle modifiche non copre più il punto in cui l'indice si era fermato " +
            $"({reason}). È stata richiesta una scansione completa; l'indice resta consultabile nel frattempo.",
            volumeId,
            ct);

        _scheduler.RequestScan(volumeId);
    }
}
