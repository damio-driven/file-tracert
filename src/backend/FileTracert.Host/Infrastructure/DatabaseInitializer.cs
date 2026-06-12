using System.Security.Cryptography;
using FileTracert.Data;
using FileTracert.Data.Entities;
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

    public DatabaseInitializer(
        IServiceProvider services,
        IApiTokenAccessor tokenAccessor,
        ILogger<DatabaseInitializer> logger)
    {
        _services = services;
        _tokenAccessor = tokenAccessor;
        _logger = logger;
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

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
