using FileTracert.Business.Notifications;
using FileTracert.Business.Scanning;
using FileTracert.Business.Setup;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Notifications;
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
        return services;
    }
}
