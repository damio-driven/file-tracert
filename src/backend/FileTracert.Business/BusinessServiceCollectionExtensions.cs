using FileTracert.Business.Scanning;
using FileTracert.Business.Setup;
using FileTracert.Business.Volumes;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Business;

/// <summary>DI wiring for the Business layer orchestrators.</summary>
public static class BusinessServiceCollectionExtensions
{
    public static IServiceCollection AddBusinessServices(this IServiceCollection services)
    {
        services.AddScoped<VolumeSyncService>();
        services.AddScoped<ScanService>();
        services.AddScoped<FilterReconciler>();
        services.AddScoped<FolderBrowseService>();
        services.AddScoped<FilterSettingsService>();
        services.AddScoped<WatchedRootsService>();
        return services;
    }
}
