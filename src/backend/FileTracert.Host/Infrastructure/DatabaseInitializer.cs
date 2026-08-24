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

            // If the FTS index holds fewer entries than there are rows that belong in it, rebuild
            // it. Runs last on purpose: the orphan reconciliation above clears Pending* columns,
            // which are part of the PROJECTED name this index stores (§5), so a rebuild placed
            // before it would index names that the very next step invalidates.
            await BackfillFtsIfNeededAsync(scope, db, _logger, ct);

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
    /// FTS backfill: if the index holds fewer entries than there are rows that belong in it,
    /// rebuild it. That covers the first startup after the search feature was introduced, the
    /// first startup after a migration that had to recreate the virtual table (14a — FTS5 has no
    /// <c>ALTER TABLE … ADD COLUMN</c>), and a rebuild that was interrupted halfway.
    ///
    /// <para>The question used to be "is the index empty", and that was wrong in a way that could
    /// not be seen from the outside. The 14a migration leaves the index empty on purpose; but
    /// <see cref="OverlayWriter.ReconcileOrphansAsync"/> runs earlier in this same startup and
    /// re-syncs the files whose orphan overlay it just cleared. Those few rows made an empty index
    /// "not empty", the rebuild was skipped, and every search from then on answered from a handful
    /// of rows — with nothing logged, and the Catalog screen still perfectly correct, so nothing
    /// pointed at the cause. Ordering the two differently would fix that one interaction; asking
    /// the right question fixes the class, and the interrupted rebuild with it.</para>
    ///
    /// <para><b>Deliberately one-sided</b>: short means rebuild, long does not. A stale entry for a
    /// row that has since been excluded or removed is already invisible — <c>SearchAsync</c>
    /// filters on <c>IsIncluded</c>/<c>IsPresent</c> — so treating "more entries than rows" as
    /// damage would buy nothing and would risk a full rebuild on every single startup.</para>
    ///
    /// <para>K12 still holds: the SQLite specifics stay behind <see cref="IFileSearchIndex"/>, the
    /// decision stays here. The cost is a full count of the index, ~250–335 ms on a 742 033-entry
    /// catalog, once per startup.</para>
    /// </summary>
    private static async Task BackfillFtsIfNeededAsync(
        IServiceScope scope, FileTracertDbContext db, ILogger logger, CancellationToken ct)
    {
        var indexable = await db.Files.CountAsync(f => f.IsIncluded && f.IsPresent, ct);
        if (indexable == 0) return;

        var fts = scope.ServiceProvider.GetRequiredService<IFileSearchIndex>();
        var indexed = await fts.CountEntriesAsync(ct);
        if (indexed >= indexable) return;

        // Logged before and after: a rebuild is the most expensive thing this startup does (~10 s
        // on a real catalog), and one that repeats every startup is a defect that must be visible
        // rather than merely slow.
        logger.LogWarning(
            "Search index is short — {Indexed} entries for {Indexable} indexable files. Rebuilding.",
            indexed, indexable);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await fts.RebuildAsync(ct);
        logger.LogInformation(
            "Search index rebuilt in {Elapsed:F1} s.", started.Elapsed.TotalSeconds);
    }

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
