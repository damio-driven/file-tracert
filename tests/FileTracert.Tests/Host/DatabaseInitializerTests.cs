using FileTracert.Data;
using FileTracert.Host.Configuration;
using FileTracert.Host.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Host;

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ft-init-{Guid.NewGuid():N}.db");

    static DatabaseInitializerTests() => SQLitePCL.Batteries.Init();

    private ServiceProvider BuildProvider(out ApiTokenAccessor token)
    {
        token = new ApiTokenAccessor();
        var services = new ServiceCollection();
        services.AddDataServices(DatabaseLocation.ConnectionString(_dbPath));
        services.AddSingleton<IApiTokenAccessor>(token);
        services.AddSingleton(sp => new DatabaseInitializer(
            sp, sp.GetRequiredService<IApiTokenAccessor>(), NullLogger<DatabaseInitializer>.Instance));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Initialize_migrates_schema_enables_wal_and_publishes_token()
    {
        await using var provider = BuildProvider(out var token);

        await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);

        // Schema applied: a known table is queryable.
        await using (var db = provider.CreateScope().ServiceProvider.GetRequiredService<FileTracertDbContext>())
        {
            (await db.Database.CanConnectAsync()).Should().BeTrue();
            (await db.AppSettings.SingleAsync()).ApiToken.Should().NotBeNullOrEmpty();
        }

        token.Token.Should().NotBeNullOrEmpty();
        ReadJournalMode().Should().Be("wal");
    }

    [Fact]
    public async Task Initialize_is_idempotent_and_keeps_the_same_token()
    {
        await using var provider = BuildProvider(out var token);
        var initializer = provider.GetRequiredService<DatabaseInitializer>();

        await initializer.InitializeAsync(CancellationToken.None);
        var first = token.Token;

        await initializer.InitializeAsync(CancellationToken.None);

        token.Token.Should().Be(first);
        ReadJournalMode().Should().Be("wal");
    }

    [Fact]
    public async Task Initialize_checkpoints_and_truncates_a_grown_wal()
    {
        await using var provider = BuildProvider(out _);
        var initializer = provider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync(CancellationToken.None);

        // Grow the WAL: bulk-write without checkpointing.
        await using (var db = provider.CreateScope().ServiceProvider.GetRequiredService<FileTracertDbContext>())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_autocheckpoint=0;");
            var payload = new string('x', 4000);
            for (int i = 0; i < 50; i++)
            {
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO Notifications (TimestampUtc, Severity, Source, Title, Message, IsRead, IsDismissed, CreatedUtc, UpdatedUtc) VALUES ('2026-01-01', 'Info', 'T', {"grow-" + i}, {payload}, 0, 0, '2026-01-01', '2026-01-01');");
            }
        }
        var grownWal = new FileInfo(_dbPath + "-wal").Length;
        grownWal.Should().BeGreaterThan(100_000);

        // Re-init (as at service startup) must merge + truncate the WAL.
        await initializer.InitializeAsync(CancellationToken.None);

        new FileInfo(_dbPath + "-wal").Length.Should().BeLessThan(grownWal / 10);
    }

    private string ReadJournalMode()
    {
        using var connection = new SqliteConnection(DatabaseLocation.ConnectionString(_dbPath));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return ((string)cmd.ExecuteScalar()!).ToLowerInvariant();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
