using FileTracert.Business;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileTracert.HardwareSmoke.Harness;

/// <summary>
/// One scenario's isolated world: its own throwaway SQLite database (outside every test volume)
/// and its own service provider wired with the product's real Data + Platform + Business
/// registrations. Isolation per scenario means a scenario's assertions describe only its own
/// fixtures, and a scenario that crashes the state machine cannot poison the next one.
/// </summary>
public sealed class ScenarioEnvironment : IAsyncDisposable
{
    private readonly Action<string> _log;

    private ScenarioEnvironment(
        ServiceProvider services, string databaseDirectory, string databasePath,
        int sourceVolumeId, int targetVolumeId, Action<string> log)
    {
        Services = services;
        DatabaseDirectory = databaseDirectory;
        DatabasePath = databasePath;
        SourceVolumeId = sourceVolumeId;
        TargetVolumeId = targetVolumeId;
        _log = log;
    }

    public ServiceProvider Services { get; }
    public string DatabaseDirectory { get; }
    public string DatabasePath { get; }
    public int SourceVolumeId { get; }
    public int TargetVolumeId { get; }

    public static async Task<ScenarioEnvironment> CreateAsync(
        VolumePair pair, string databaseDirectory, LogLevel minimumLogLevel, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(databaseDirectory);
        var databasePath = Path.Combine(databaseDirectory, "harness.db");

        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(minimumLogLevel));
        services.AddDataServices($"Data Source={databasePath}");
        services.AddPlatformServices();
        services.AddBusinessServices();
        services.AddScoped<CatalogArranger>();

        var provider = services.BuildServiceProvider();

        try
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            await db.Database.MigrateAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

            var probe = scope.ServiceProvider.GetRequiredService<IVolumeProbe>();
            var sourceVolumeId = await EnsureVolumeAsync(db, probe, pair.Source, ct);
            var targetVolumeId = pair.IsCrossVolume
                ? await EnsureVolumeAsync(db, probe, pair.Target, ct)
                : sourceVolumeId;

            return new ScenarioEnvironment(
                provider, databaseDirectory, databasePath, sourceVolumeId, targetVolumeId, log);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Inserts the <see cref="Volume"/> row for a test area from the live probe, using the
    /// product's own <see cref="VolumeMapper"/> so capacity, free bytes, filesystem and kind are
    /// derived exactly as the service derives them.
    /// </summary>
    private static async Task<int> EnsureVolumeAsync(
        FileTracertDbContext db, IVolumeProbe probe, TestVolume volume, CancellationToken ct)
    {
        var existing = await db.Volumes.FirstOrDefaultAsync(v => v.VolumeGuid == volume.VolumeGuid, ct);
        if (existing is not null) return existing.Id;

        var probed = probe.TryGetByGuid(volume.VolumeGuid)
            ?? throw new InvalidOperationException(
                $"Test volume '{volume.Name}' ({volume.VolumeGuid}) is no longer present on the system.");

        var entity = VolumeMapper.MapNew(probed, DateTime.UtcNow);
        entity.IsOnline = true;
        entity.IsCatalogable = true;
        db.Volumes.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    /// <summary>Runs a unit of work on a fresh scope, like an API request would.</summary>
    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    public Task<T> WithDbAsync<T>(Func<FileTracertDbContext, Task<T>> work) =>
        WithScopeAsync(sp => work(sp.GetRequiredService<FileTracertDbContext>()));

    public async Task WithDbAsync(Func<FileTracertDbContext, Task> work) =>
        await WithScopeAsync<object?>(async sp =>
        {
            await work(sp.GetRequiredService<FileTracertDbContext>());
            return null;
        });

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();

        // Microsoft.Data.Sqlite pools connections: without this the .db file stays locked and the
        // delete below fails, leaving a stack of throwaway databases behind.
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(DatabaseDirectory))
                Directory.Delete(DatabaseDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            // Not silent (§9): the run continues, but the operator is told what was left behind.
            _log($"could not delete the scenario database at '{DatabaseDirectory}': {ex.GetType().Name}: {ex.Message}");
        }
    }
}
