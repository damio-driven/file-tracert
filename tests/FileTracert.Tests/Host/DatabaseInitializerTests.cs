using FileTracert.Business;
using FileTracert.Data;
using FileTracert.Host.Configuration;
using FileTracert.Host.Infrastructure;
using FileTracert.Tests.Infrastructure;
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
        services.AddLogging();
        services.AddDataServices(DatabaseLocation.ConnectionString(_dbPath));
        // The initializer also runs the §5 orphan-overlay reconciliation, which lives in
        // Business — registered here exactly as Program.cs registers it.
        services.AddBusinessServices();
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

    /// <summary>
    /// The FTS backfill has to answer "is the index WHOLE", not "does it have a row in it".
    ///
    /// <para>Step 14a leaves the index empty after its migration and lets this backfill refill it.
    /// That is safe only if nothing can put a row into the empty index first — and something can:
    /// <c>OverlayWriter.ReconcileOrphansAsync</c> runs in this same startup and re-syncs the files
    /// whose orphan overlay it just cleared. With an "is there at least one row" guard, an
    /// installation whose last shutdown left one pending overlay behind — exactly the case that
    /// reconciliation exists for — would upgrade, get a handful of entries written into the empty
    /// index, skip the rebuild, and answer every search from those few rows. For every startup
    /// afterwards, too, with nothing logged and the Catalog screen still perfectly right, so
    /// nothing would point at the cause.</para>
    ///
    /// <para>The same guard covers the other way to end up short, which the 14a migration also
    /// makes reachable for the first time: a rebuild interrupted halfway (it takes ~10 s on a real
    /// catalog) leaves a non-empty, incomplete index that "is it empty" would never repair.</para>
    ///
    /// <para>This drives it through the real path: a real orphan overlay, the real reconciliation,
    /// the real initializer.</para>
    /// </summary>
    [Fact]
    public async Task Backfill_rebuilds_an_index_that_is_incomplete_not_only_one_that_is_empty()
    {
        await using var provider = BuildProvider(out _);
        var initializer = provider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync(CancellationToken.None);

        const int fileCount = 20;
        int orphanFileId;

        await using (var db = provider.CreateScope().ServiceProvider.GetRequiredService<FileTracertDbContext>())
        {
            var volume = new FileTracert.Data.Entities.Volume
            {
                VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
                FileSystem = "NTFS",
                ScanEngine = FileTracert.Contracts.Enums.VolumeScanEngine.UsnJournal,
                IsOnline = true,
            };
            db.Volumes.Add(volume);
            await db.SaveChangesAsync();

            var root = new FileTracert.Data.Entities.DirectoryNode
            {
                VolumeId = volume.Id,
                Name = string.Empty,
                MaterializedPath = string.Empty,
                IsMaterialized = true,
            };
            db.Directories.Add(root);
            await db.SaveChangesAsync();

            for (var i = 0; i < fileCount; i++)
            {
                db.Files.Add(new FileTracert.Data.Entities.FileEntry
                {
                    VolumeId = volume.Id,
                    DirectoryId = root.Id,
                    Name = $"file{i}.jpg",
                    Extension = "jpg",
                    Category = FileTracert.Contracts.Enums.FileCategory.Image,
                    SizeBytes = 1,
                    IsIncluded = true,
                    IsPresent = true,
                });
            }
            await db.SaveChangesAsync();

            // The upgrade's starting point: the migration has just recreated the table empty.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM FileSearchIndex");

            // And the thing that can write into it first: one file left carrying an overlay whose
            // job no longer exists. ReconcileOrphansAsync clears it and re-syncs that one file.
            var orphan = await db.Files.OrderBy(f => f.Id).FirstAsync();
            orphanFileId = orphan.Id;
            orphan.PendingState = FileTracert.Contracts.Enums.EntityPendingState.PendingRename;
            orphan.PendingName = "renamed.jpg";
            orphan.PendingJobId = 999_999;      // no such job
            await db.SaveChangesAsync();
        }

        await initializer.InitializeAsync(CancellationToken.None);

        await using (var db = provider.CreateScope().ServiceProvider.GetRequiredService<FileTracertDbContext>())
        {
            // The reconciliation really did run — otherwise this test would be proving nothing
            // about the interaction it exists to pin down.
            (await db.Files.SingleAsync(f => f.Id == orphanFileId)).PendingState
                .Should().Be(FileTracert.Contracts.Enums.EntityPendingState.None);

            var indexed = await CountIndexEntriesAsync(db);
            indexed.Should().Be(fileCount,
                "the backfill must rebuild an index that is short, not only one that is empty");
        }
    }

    private static async Task<long> CountIndexEntriesAsync(FileTracertDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM FileSearchIndex";
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
        finally { db.Database.CloseConnection(); }
    }

    private string ReadJournalMode()
    {
        using var connection = new SqliteConnection(DatabaseLocation.ConnectionString(_dbPath));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return ((string)cmd.ExecuteScalar()!).ToLowerInvariant();
    }

    /// <summary>
    /// Releases only this test's own pool. Clearing every pool in the process would dispose
    /// the native handle of whatever another test class is querying — and, in the other
    /// direction, would checkpoint and drop the WAL this class is measuring (see
    /// <see cref="SqliteTestDatabase"/>).
    /// </summary>
    public void Dispose() => SqliteTestDatabase.Delete(_dbPath);
}
