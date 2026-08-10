using FileTracert.Data.Conversions;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Data;

public class FileTracertDbContext : DbContext
{
    public FileTracertDbContext(DbContextOptions<FileTracertDbContext> options)
        : base(options)
    {
    }

    public DbSet<Volume> Volumes => Set<Volume>();
    public DbSet<WatchedRoot> WatchedRoots => Set<WatchedRoot>();
    public DbSet<DirectoryNode> Directories => Set<DirectoryNode>();
    public DbSet<FileEntry> Files => Set<FileEntry>();
    public DbSet<OperationJob> OperationJobs => Set<OperationJob>();
    public DbSet<OperationJobItem> OperationJobItems => Set<OperationJobItem>();
    public DbSet<SpaceLedgerEntry> SpaceLedgerEntries => Set<SpaceLedgerEntry>();
    public DbSet<ExtensionCategory> ExtensionCategories => Set<ExtensionCategory>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>
    /// One line per CLR type instead of a converter on every timestamp property: all dates
    /// are UTC (§6), and the store cannot remember that on its own. Registering it for
    /// <see cref="DateTime"/> covers <c>DateTime?</c> too.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FileTracertDbContext).Assembly);
    }
}
