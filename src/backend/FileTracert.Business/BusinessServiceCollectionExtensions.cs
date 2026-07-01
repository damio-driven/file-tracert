using FileTracert.Business.Notifications;
using FileTracert.Business.Operations;
using FileTracert.Business.Scanning;
using FileTracert.Business.Setup;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Notifications;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Scanning;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Business;

/// <summary>DI wiring for the Business layer orchestrators.</summary>
public static class BusinessServiceCollectionExtensions
{
    public static IServiceCollection AddBusinessServices(this IServiceCollection services)
    {
        services.AddScoped<VolumeSyncService>();
        services.AddScoped<ScanService>();

        // The scan-progress tracker is volatile in-memory state shared across scopes:
        // the worker's ScanService writes it, the API reads it → singleton.
        services.AddSingleton<IScanStatusTracker, ScanStatusTracker>();
        services.AddScoped<FilterReconciler>();
        services.AddScoped<FolderBrowseService>();
        services.AddScoped<FilterSettingsService>();
        services.AddScoped<WatchedRootsService>();
        services.AddScoped<INotificationPublisher, NotificationService>();

        // Space ledger is a singleton: preview (API threads) and processor both read/write it.
        services.AddSingleton<ISpaceLedger, SpaceLedger>();

        // Cancellation registry is a singleton: the API (Cancel) signals the token the worker
        // is running the job under, across two different DbContexts.
        services.AddSingleton<IJobCancellationRegistry, JobCancellationRegistry>();

        // Queue service is scoped: each API request gets its own DbContext.
        services.AddScoped<IQueueService, QueueService>();

        // Execution engine + index updater are scoped: resolved per job execution.
        services.AddScoped<IndexUpdater>();
        services.AddScoped<JobExecutionEngine>();
        return services;
    }
}
