using FileTracert.Contracts.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Platform;

/// <summary>
/// DI wiring for the Platform layer. The Host calls this to bind the port
/// interfaces to their Win32 implementations.
/// </summary>
public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        services.AddSingleton<IPhysicalDiskResolver, WmiPhysicalDiskResolver>();
        services.AddSingleton<IVolumeProbe, Win32VolumeProbe>();
        services.AddSingleton<IUsnReader, NtfsUsnReader>();
        services.AddSingleton<IDirectoryEnumerator, ManagedDirectoryEnumerator>();
        return services;
    }
}
