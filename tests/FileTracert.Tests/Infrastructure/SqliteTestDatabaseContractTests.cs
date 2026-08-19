using FileTracert.Contracts.Logging;
using FileTracert.Data;
using FileTracert.Data.Logging;
using FileTracert.Host.Configuration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Infrastructure;

/// <summary>
/// The contract that makes the step 11i fix safe: a <em>targeted</em> <c>ClearPool</c> works on
/// one connection string, so it frees the file only if it names the pool the code under test
/// really opened. Getting that wrong fails in silence — the deletes simply start missing, the
/// suite stays green, and %TEMP% fills with locked databases.
/// <para>
/// Both halves of the product open SQLite their own way: EF Core through
/// <c>AddDataServices</c>, and the log store through raw <see cref="SqliteLogStore"/>. Each one
/// gets its own fact, built from <see cref="DatabaseLocation"/> exactly as <c>Program.cs</c>
/// builds it, so a future parameter on that helper turns this red instead of turning the
/// cleanup into a no-op.
/// </para>
/// </summary>
public sealed class SqliteTestDatabaseContractTests
{
    static SqliteTestDatabaseContractTests() => SQLitePCL.Batteries.Init();

    [Fact]
    public async Task Delete_releases_the_pool_EF_Core_opened()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ft-contract-ef-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataServices(DatabaseLocation.ConnectionString(path));
        await using (var provider = services.BuildServiceProvider())
        {
            await using var db = provider.GetRequiredService<FileTracertDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // The context is gone, but its connection is not: it went back to the pool, still
        // holding the file open. Only clearing *that* pool lets the delete through.
        SqliteTestDatabase.Delete(path);

        File.Exists(path).Should()
            .BeFalse("SqliteTestDatabase must name the same pool AddDataServices opened");
    }

    [Fact]
    public async Task Delete_releases_the_pool_the_log_store_opened()
    {
        var mainPath = Path.Combine(Path.GetTempPath(), $"ft-contract-log-{Guid.NewGuid():N}.db");
        var logsPath = DatabaseLocation.ResolveLogs(mainPath);

        var store = new SqliteLogStore(DatabaseLocation.ConnectionString(logsPath));
        store.EnsureSchema();
        await store.WriteBatchAsync(
            [new LogRecord(DateTime.UtcNow, 2, "Test", "held open", null, null, null)],
            CancellationToken.None);

        SqliteTestDatabase.Delete(logsPath);

        File.Exists(logsPath).Should()
            .BeFalse("the log database has its own connection string, and its own pool");
    }

    /// <summary>
    /// The path rule too, not just the connection string: the factory derives the log database
    /// from the main one, and a copy of that rule that drifts would clear a pool nobody uses.
    /// </summary>
    [Fact]
    public void The_log_database_path_is_the_one_the_host_resolves()
    {
        using var factory = new FileTracert.Tests.Host.FileTracertAppFactory();

        factory.LogDatabasePath.Should().Be(DatabaseLocation.ResolveLogs(factory.DatabasePath));
    }
}
