using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Host.Infrastructure;
using FileTracert.Host.Workers;
using FileTracert.Tests.Business;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileTracert.Tests.Host;

/// <summary>
/// Hosts the real <c>Program</c> over a temporary on-disk SQLite database (so WAL
/// and migrations behave like production) with the Platform ports swapped for
/// hand-written fakes and an optional deterministic seeder.
/// </summary>
public sealed class FileTracertAppFactory : WebApplicationFactory<global::Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ft-test-{Guid.NewGuid():N}.db");

    public IVolumeProbe Probe { get; set; } = new FakeVolumesProbe([]);
    public IUsnReader UsnReader { get; set; } = new FakeUsnReader([], 0);
    public IDirectoryEnumerator DirectoryEnumerator { get; set; } = new FakeDirectoryEnumerator([]);
    public IFileMetadataReader MetadataReader { get; set; } = new FakeFileMetadataReader(new Dictionary<string, FileMetadata>());
    public IFileSystemBrowser FileSystemBrowser { get; set; } =
        new FakeFileSystemBrowser(new Dictionary<string, IReadOnlyList<FolderNode>>());
    public Func<FileTracertDbContext, CancellationToken, Task>? Seed { get; set; }

    public int VolumeSyncIntervalSeconds { get; set; } = 1;
    public int ScanPollIntervalSeconds { get; set; } = 1;

    /// <summary>Hosting environment; flip to "Production" to assert dev-only wiring is absent.</summary>
    public string EnvironmentName { get; set; } = "Development";

    /// <summary>Drop a worker so a focused test isn't disturbed by the other one's DB writes.</summary>
    public bool DisableVolumeSync { get; set; }
    public bool DisableScan { get; set; }
    public bool DisableQueue { get; set; }

    public string Token => Services.GetRequiredService<IApiTokenAccessor>().Token!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting("FileTracert:DatabasePath", _dbPath);
        builder.UseSetting("FileTracert:VolumeSyncIntervalSeconds", VolumeSyncIntervalSeconds.ToString());
        builder.UseSetting("FileTracert:ScanPollIntervalSeconds", ScanPollIntervalSeconds.ToString());

        builder.ConfigureTestServices(services =>
        {
            Replace<IVolumeProbe>(services, Probe);
            Replace<IUsnReader>(services, UsnReader);
            Replace<IDirectoryEnumerator>(services, DirectoryEnumerator);
            Replace<IFileMetadataReader>(services, MetadataReader);
            Replace<IFileSystemBrowser>(services, FileSystemBrowser);

            if (Seed is not null)
            {
                services.AddSingleton<IStartupSeeder>(new DelegateSeeder(Seed));
            }

            if (DisableVolumeSync)
            {
                RemoveHostedService<VolumeSyncWorker>(services);
            }

            if (DisableScan)
            {
                RemoveHostedService<ScanWorker>(services);
            }

            if (DisableQueue)
            {
                RemoveHostedService<QueueProcessorWorker>(services);
            }
        });
    }

    private static void RemoveHostedService<T>(IServiceCollection services)
        where T : IHostedService
    {
        var descriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T))
            .ToList();
        foreach (var d in descriptors)
        {
            services.Remove(d);
        }
    }

    private static void Replace<T>(IServiceCollection services, T instance)
        where T : class
    {
        var existing = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in existing)
        {
            services.Remove(d);
        }

        services.AddSingleton(instance);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var logsPath = Path.Combine(
                Path.GetDirectoryName(_dbPath)!,
                Path.GetFileNameWithoutExtension(_dbPath) + "-logs.db");

            foreach (var basePath in new[] { _dbPath, logsPath })
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    try
                    {
                        File.Delete(basePath + suffix);
                    }
                    catch
                    {
                        // best-effort temp cleanup
                    }
                }
            }
        }
    }

    private sealed class DelegateSeeder(Func<FileTracertDbContext, CancellationToken, Task> seed) : IStartupSeeder
    {
        public Task SeedAsync(FileTracertDbContext db, CancellationToken ct) => seed(db, ct);
    }
}
