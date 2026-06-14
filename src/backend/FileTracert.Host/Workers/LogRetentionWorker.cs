using FileTracert.Contracts.Logging;
using FileTracert.Data;
using FileTracert.Host.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileTracert.Host.Workers;

/// <summary>
/// Periodically trims the log database so it never grows without bound: removes
/// entries older than <c>AppSettings.LogRetentionDays</c> and caps total rows at
/// <c>AppSettings.LogMaxRows</c>, reclaiming space with an occasional VACUUM. A
/// failed cycle is logged and retried next interval.
/// </summary>
public sealed class LogRetentionWorker : BackgroundService
{
    private readonly ILogStore _logStore;
    private readonly IServiceProvider _services;
    private readonly TimeSpan _interval;
    private readonly ILogger<LogRetentionWorker> _logger;

    public LogRetentionWorker(
        ILogStore logStore,
        IServiceProvider services,
        IOptions<FileTracertOptions> options,
        ILogger<LogRetentionWorker> logger)
    {
        _logStore = logStore;
        _services = services;
        _interval = TimeSpan.FromSeconds(Math.Max(60, options.Value.LogRetentionIntervalSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrimOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Log retention cycle failed; will retry next interval.");
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

    private async Task TrimOnceAsync(CancellationToken ct)
    {
        int retentionDays;
        int maxRows;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var settings = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            retentionDays = settings?.LogRetentionDays ?? 14;
            maxRows = settings?.LogMaxRows ?? 500_000;
        }

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
        var removed = await _logStore.TrimAsync(cutoff, maxRows, vacuum: true, ct);
        if (removed > 0)
        {
            _logger.LogDebug("Log retention removed {Removed} entries.", removed);
        }
    }
}
