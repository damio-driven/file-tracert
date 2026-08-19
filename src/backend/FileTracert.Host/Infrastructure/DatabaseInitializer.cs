using System.Security.Cryptography;
using FileTracert.Business.Projection;
using FileTracert.Contracts.Logging;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Host.Logging;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Infrastructure;

/// <summary>
/// One-shot startup initialization, run before the host accepts requests:
/// apply migrations (creating the DB if absent), switch the file to WAL journal
/// mode, ensure the <see cref="AppSettings"/> singleton exists with a loopback
/// API token, and publish that token to <see cref="IApiTokenAccessor"/>. Any
/// failure throws so the host stops cleanly instead of serving on a broken DB.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly IServiceProvider _services;
    private readonly IApiTokenAccessor _tokenAccessor;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly LogLevelSwitch? _logLevelSwitch;

    public DatabaseInitializer(
        IServiceProvider services,
        IApiTokenAccessor tokenAccessor,
        ILogger<DatabaseInitializer> logger,
        LogLevelSwitch? logLevelSwitch = null)
    {
        _services = services;
        _tokenAccessor = tokenAccessor;
        _logger = logger;
        _logLevelSwitch = logLevelSwitch;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();

        try
        {
            _logger.LogInformation("Applying database migrations…");
            await db.Database.MigrateAsync(ct);

            // WAL is persistent in the file; running it every startup is cheap and idempotent.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

            // Merge the WAL back into the main file and truncate it. Under constant read
            // traffic passive auto-checkpoints can starve forever (observed: a 146 MB WAL
            // never merged for days → every write slow, "database is locked" timeouts).
            // Startup is the one moment with no concurrent readers, so force it here, and
            // cap the WAL size going forward so auto-checkpoints keep it bounded.
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_size_limit=67108864;", ct);

            var token = await EnsureSettingsTokenAsync(db, ct);
            _tokenAccessor.Set(token);

            var seeder = scope.ServiceProvider.GetService<IStartupSeeder>();
            if (seeder is not null)
            {
                await seeder.SeedAsync(db, ct);
            }

            // §5 safety net, before any worker runs: an overlay whose job no longer exists or
            // is already terminal shows a file in a folder it will never reach. Every write and
            // every clear runs inside the transaction of the job's own state change, so nothing
            // should produce one — but a crash outside those transactions, or a database from an
            // older build, can. One query per table.
            await scope.ServiceProvider.GetRequiredService<OverlayWriter>().ReconcileOrphansAsync(ct);

            // Apply the persisted minimum log level to the runtime switch (read after
            // the seeder so a seeded override is honored).
            await ApplyLogLevelAsync(db, ct);

            // If Files exist but the FTS index is empty (e.g. first start after this
            // feature was added), do a one-time full backfill.
            await BackfillFtsIfNeededAsync(scope, db, ct);

            _logger.LogInformation("Database initialized (migrated, WAL on).");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Database initialization failed; the service will not start.");
            throw;
        }
    }

    private static async Task<string> EnsureSettingsTokenAsync(FileTracertDbContext db, CancellationToken ct)
    {
        var settings = await db.AppSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new AppSettings
            {
                DefaultExtensionFilter = [],
                ExcludedPaths = ["Windows", "Program Files", "Program Files (x86)", "$Recycle.Bin", "AppData"],
                ApiToken = GenerateToken(),
                SpaceMarginPercent = 3,
            };
            db.AppSettings.Add(settings);
        }
        else if (string.IsNullOrEmpty(settings.ApiToken))
        {
            settings.ApiToken = GenerateToken();
        }

        await db.SaveChangesAsync(ct);
        return settings.ApiToken;
    }

    private async Task ApplyLogLevelAsync(FileTracertDbContext db, CancellationToken ct)
    {
        if (_logLevelSwitch is null)
        {
            return;
        }

        var name = await db.AppSettings.AsNoTracking()
            .Select(s => s.MinimumLogLevel)
            .FirstOrDefaultAsync(ct);

        if (LogLevelNames.TryParse(name) is { } level)
        {
            _logLevelSwitch.Current = (Microsoft.Extensions.Logging.LogLevel)level;
            _logger.LogInformation("Minimum log level set to {Level}.", name);
        }
    }

    /// <summary>
    /// One-time FTS backfill: if the database has indexed files but the FTS5 table is
    /// empty (first startup after the search feature was introduced), rebuild the index.
    /// Subsequent startups are cheap — the emptiness probe stops at the first row.
    ///
    /// <para>K12: both questions are now asked through an abstraction. The probe used to be a
    /// cast to <c>SqliteConnection</c> and a hand-written statement against an FTS5 virtual
    /// table, in <c>Host</c> — SQLite leaking straight through the boundary §3 puts around it.</para>
    /// </summary>
    private static async Task BackfillFtsIfNeededAsync(
        IServiceScope scope, FileTracertDbContext db, CancellationToken ct)
    {
        var hasFiles = await db.Files.AnyAsync(f => f.IsIncluded && f.IsPresent, ct);
        if (!hasFiles) return;

        var fts = scope.ServiceProvider.GetRequiredService<IFileSearchIndex>();
        if (await fts.IsEmptyAsync(ct))
            await fts.RebuildAsync(ct);
    }

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
