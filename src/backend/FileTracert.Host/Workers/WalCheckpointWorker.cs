using FileTracert.Contracts.Logging;
using FileTracert.Data;
using FileTracert.Host.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileTracert.Host.Workers;

/// <summary>
/// Periodically merges the write-ahead log back into the main and log databases and
/// truncates it, so the WAL stays bounded during long runs. SQLite's passive
/// auto-checkpoint can starve under constant read traffic, letting the WAL grow without
/// bound (observed: 555 MB main + 185 MB log); a bloated WAL slows every write and widens
/// the window in which a concurrent writer times out with "database is locked". The startup
/// checkpoint in <see cref="Infrastructure.DatabaseInitializer"/> only runs once — this keeps
/// it bounded thereafter. Best-effort: a failed cycle is logged and retried next interval.
/// </summary>
public sealed class WalCheckpointWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogStore _logStore;
    private readonly TimeSpan _interval;
    private readonly ILogger<WalCheckpointWorker> _logger;

    public WalCheckpointWorker(
        IServiceProvider services,
        ILogStore logStore,
        IOptions<FileTracertOptions> options,
        ILogger<WalCheckpointWorker> logger)
    {
        _services = services;
        _logStore = logStore;
        _interval = TimeSpan.FromSeconds(Math.Max(10, options.Value.WalCheckpointIntervalSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait first: startup already checkpoints both DBs, so the first cycle is only
            // due after one interval of accumulated WAL.
            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await CheckpointOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WAL checkpoint cycle failed; will retry next interval.");
            }
        }
    }

    private async Task CheckpointOnceAsync(CancellationToken ct)
    {
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
        }

        await _logStore.CheckpointAsync(ct);
        _logger.LogDebug("WAL checkpoint cycle completed.");
    }
}
