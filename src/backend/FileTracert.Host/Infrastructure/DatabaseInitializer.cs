using System.Security.Cryptography;
using FileTracert.Contracts.Logging;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Host.Logging;
using Microsoft.Data.Sqlite;
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

            var token = await EnsureSettingsTokenAsync(db, ct);
            _tokenAccessor.Set(token);

            var seeder = scope.ServiceProvider.GetService<IStartupSeeder>();
            if (seeder is not null)
            {
                await seeder.SeedAsync(db, ct);
            }

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
    /// Subsequent startups are cheap — the COUNT query returns quickly on a non-empty table.
    /// </summary>
    private static async Task BackfillFtsIfNeededAsync(
        IServiceScope scope, FileTracertDbContext db, CancellationToken ct)
    {
        var hasFiles = await db.Files.AnyAsync(f => f.IsIncluded && f.IsPresent, ct);
        if (!hasFiles) return;

        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        long ftsCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM FileSearchIndex)";
            ftsCount = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        if (ftsCount == 0)
        {
            var fts = scope.ServiceProvider.GetRequiredService<IFileSearchIndex>();
            await fts.RebuildAsync(ct);
        }
    }

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
