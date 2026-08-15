using FileTracert.Business.Notifications;
using FileTracert.Business.Operations;
using FileTracert.Business.Projection;
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
        services.AddScoped<DirectoryMerger>();

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

        // Queue signal is a singleton: enqueue/retry (API threads) wake the processor worker so it
        // idles on a signal instead of polling the DB on a fixed interval.
        services.AddSingleton<IQueueSignal, QueueSignal>();

        // Queue service is scoped: each API request gets its own DbContext.
        services.AddScoped<IQueueService, QueueService>();

        // Projection (§5): the overlay writer is the single point that stamps and clears the
        // Pending* fields, the directory resolver the single walk that materializes a path.
        services.AddScoped<DirectoryResolver>();
        services.AddScoped<OverlayWriter>();
        services.AddScoped<ProjectedPathResolver>();

        // Execution engine + index updater are scoped: resolved per job execution.
        services.AddScoped<IndexUpdater>();
        services.AddScoped<JobExecutionEngine>();

        // Revaluator runs after every completed job to wake Blocked(InsufficientSpace) jobs.
        services.AddScoped<BlockedJobRevaluator>();

        // Real wall clock for the engine's copy-progress throttle; tests substitute a fake.
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
