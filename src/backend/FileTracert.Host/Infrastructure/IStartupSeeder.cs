using FileTracert.Data;

namespace FileTracert.Host.Infrastructure;

/// <summary>
/// Optional hook invoked by <see cref="DatabaseInitializer"/> after the schema is
/// migrated but before any worker starts. Production registers none; tests use it
/// to seed deterministic data before the background services run.
/// </summary>
public interface IStartupSeeder
{
    Task SeedAsync(FileTracertDbContext db, CancellationToken ct);
}
