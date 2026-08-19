using FileTracert.Business.Filtering;
using FileTracert.Business.Notifications;
using FileTracert.Business.Operations;
using FileTracert.Business.Projection;
using FileTracert.Business.Realtime;
using FileTracert.Business.Scanning;
using FileTracert.Business.Setup;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Notifications;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Realtime;
using FileTracert.Contracts.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // "Which filter governs this path?" for the single-file decisions outside the scan
        // pipeline (a rename re-checking its own inclusion, C19).
        services.AddScoped<RootFilterResolver>();
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

        // The single "is anything already working on this place?" predicate (finding 8 / K5),
        // and the one path that puts a parked job back in the queue (guard re-ask, fresh
        // snapshots, overlay) shared by the revaluation and the user's Riprova.
        services.AddScoped<PendingWorkGuard>();
        services.AddScoped<JobSnapshotRefresher>();
        services.AddScoped<JobUnblocker>();

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

        // Real-time (§7). The transport is a Host concern, so Business binds the no-op port by
        // default — the harness and migration-only startups have no client to talk to — and Host
        // REPLACES it with the SignalR-backed one. TryAdd, so an implementation registered before
        // this call is left alone. RealtimeEvents is the single guarded gateway (§9).
        services.TryAddSingleton<IRealtimePublisher, NullRealtimePublisher>();
        services.AddSingleton<RealtimeEvents>();

        // Real wall clock for the engine's copy-progress throttle; tests substitute a fake.
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
