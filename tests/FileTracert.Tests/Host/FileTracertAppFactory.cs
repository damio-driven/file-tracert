using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Host.Configuration;
using FileTracert.Host.Infrastructure;
using FileTracert.Host.Workers;
using FileTracert.Tests.Business;
using FileTracert.Tests.Infrastructure;
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
    private readonly string _dbPath;

    public FileTracertAppFactory()
        : this(Path.Combine(Path.GetTempPath(), $"ft-test-{Guid.NewGuid():N}.db"))
    {
    }

    /// <summary>
    /// Hosts over an explicit database file, so a test can stop one host and start another on the
    /// same catalog — the only honest way to assert that what survives a restart is what was
    /// written to disk. The first host of such a pair must set
    /// <see cref="KeepDatabaseOnDispose"/>, or it takes the catalog with it.
    /// </summary>
    public FileTracertAppFactory(string databasePath) => _dbPath = databasePath;

    /// <summary>The temporary main database this host runs on. Deleted on dispose.</summary>
    public string DatabasePath => _dbPath;

    /// <summary>Leaves the database file behind for a second host to open. See the constructor.</summary>
    public bool KeepDatabaseOnDispose { get; set; }

    /// <summary>
    /// The dedicated log database, resolved by the host's own rule rather than by a copy of it:
    /// teardown has to release the pool the host really opened, and a second implementation that
    /// drifts would free nothing, in silence.
    /// </summary>
    public string LogDatabasePath => DatabaseLocation.ResolveLogs(_dbPath);

    public IVolumeProbe Probe { get; set; } = new FakeVolumesProbe([]);
    public IUsnReader UsnReader { get; set; } = new FakeUsnReader([], 0);
    public IDirectoryEnumerator DirectoryEnumerator { get; set; } = new FakeDirectoryEnumerator([]);
    public IFileMetadataReader MetadataReader { get; set; } = new FakeFileMetadataReader(new Dictionary<string, FileMetadata>());
    public IFileSystemBrowser FileSystemBrowser { get; set; } =
        new FakeFileSystemBrowser(new Dictionary<string, IReadOnlyList<FolderNode>>());
    public IDeviceWatcher DeviceWatcher { get; set; } = new FakeDeviceWatcher();
    public Func<FileTracertDbContext, CancellationToken, Task>? Seed { get; set; }


    public int VolumeSyncIntervalSeconds { get; set; } = 1;
    public int ScanPollIntervalSeconds { get; set; } = 1;
    public int UsnSyncIntervalSeconds { get; set; } = 1;

    /// <summary>Short by default: tests raise their whole burst in a tight loop.</summary>
    public int DeviceChangeDebounceMilliseconds { get; set; } = 200;

    /// <summary>Hosting environment; flip to "Production" to assert dev-only wiring is absent.</summary>
    public string EnvironmentName { get; set; } = "Development";

    /// <summary>Drop a worker so a focused test isn't disturbed by the other one's DB writes.</summary>
    public bool DisableVolumeSync { get; set; }
    public bool DisableScan { get; set; }
    public bool DisableUsnSync { get; set; }
    public bool DisableQueue { get; set; }
    public bool DisableDeviceWatcher { get; set; }

    public string Token => Services.GetRequiredService<IApiTokenAccessor>().Token!;

    /// <summary>
    /// The host itself, so a test can run its real STOP sequence. <c>Dispose</c> only disposes the
    /// host (see step 11i), and nothing here calls <c>RunAsync</c>, so <c>StopApplication</c> has no
    /// listener: a test about what happens while the host stops has to stop it by hand.
    /// </summary>
    public IHost RunningHost
    {
        get
        {
            _ = Services; // forces the host to be built
            return _host ?? throw new InvalidOperationException("the host has not been created");
        }
    }

    private IHost? _host;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        _host = base.CreateHost(builder);
        return _host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting("FileTracert:DatabasePath", _dbPath);
        builder.UseSetting("FileTracert:VolumeSyncIntervalSeconds", VolumeSyncIntervalSeconds.ToString());
        builder.UseSetting("FileTracert:ScanPollIntervalSeconds", ScanPollIntervalSeconds.ToString());
        builder.UseSetting("FileTracert:UsnSyncIntervalSeconds", UsnSyncIntervalSeconds.ToString());
        builder.UseSetting(
            "FileTracert:DeviceChangeDebounceMilliseconds",
            DeviceChangeDebounceMilliseconds.ToString());

        builder.ConfigureTestServices(services =>
        {
            Replace<IVolumeProbe>(services, Probe);
            Replace<IUsnReader>(services, UsnReader);
            Replace<IDirectoryEnumerator>(services, DirectoryEnumerator);
            Replace<IFileMetadataReader>(services, MetadataReader);
            Replace<IFileSystemBrowser>(services, FileSystemBrowser);
            Replace<IDeviceWatcher>(services, DeviceWatcher);

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

            if (DisableUsnSync)
            {
                RemoveHostedService<UsnSyncWorker>(services);
            }

            if (DisableQueue)
            {
                RemoveHostedService<QueueProcessorWorker>(services);
            }

            if (DisableDeviceWatcher)
            {
                RemoveHostedService<DeviceWatcherWorker>(services);
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
        if (disposing && !KeepDatabaseOnDispose)
        {
            // Only this host's two databases: the pool registry is process-wide and other
            // test classes are querying theirs right now (see SqliteTestDatabase).
            SqliteTestDatabase.Delete(DatabasePath, LogDatabasePath);
        }
    }

    private sealed class DelegateSeeder(Func<FileTracertDbContext, CancellationToken, Task> seed) : IStartupSeeder
    {
        public Task SeedAsync(FileTracertDbContext db, CancellationToken ct) => seed(db, ct);
    }
}
