using FileTracert.Data;
using FileTracert.Data.Cancellation;
using FileTracert.Data.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Data;

/// <summary>
/// Owns a real in-memory SQLite connection (kept open for the lifetime of the
/// handle so the schema survives) and produces fresh <see cref="FileTracertDbContext"/>
/// instances backed by it. The auditing interceptor can be toggled off to test
/// the non-audited path.
/// </summary>
public sealed class SqliteInMemoryContext : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _withAuditing;

    static SqliteInMemoryContext()
    {
        SQLitePCL.Batteries.Init();
    }

    /// <param name="connection">An already-built connection to use instead of a plain one —
    /// how a test observes what the raw-SQL paths do, since their commands never reach EF's
    /// interceptors. It must not be open yet; this type owns its lifetime.</param>
    public SqliteInMemoryContext(
        bool withAuditing = true, bool ensureCreated = true, SqliteConnection? connection = null)
    {
        _withAuditing = withAuditing;
        _connection = connection ?? new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        if (ensureCreated)
        {
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }
    }

    /// <summary>
    /// The host's shutdown signal, as seen by the read guard (14b). Set it to observe what a read
    /// does while the process is stopping; by default it never fires.
    /// </summary>
    public DatabaseShutdownSignal ShutdownSignal { get; set; } = DatabaseShutdownSignal.None;

    /// <param name="extraInterceptors">Test-specific interceptors (e.g. fault/race injection)
    /// appended after the standard auditing interceptor.</param>
    public FileTracertDbContext CreateContext(params IInterceptor[] extraInterceptors)
    {
        var builder = new DbContextOptionsBuilder<FileTracertDbContext>()
            .UseSqlite(_connection)
            // Same interceptor AddDataServices wires in production: without it a test would be
            // measuring a DbContext the product never builds.
            .AddInterceptors(new SqliteReadCancellationInterceptor(
                ShutdownSignal, NullLogger<SqliteReadCancellationInterceptor>.Instance));

        if (_withAuditing)
        {
            builder.AddInterceptors(new AuditingSaveChangesInterceptor());
        }

        if (extraInterceptors.Length > 0)
        {
            builder.AddInterceptors(extraInterceptors);
        }

        return new FileTracertDbContext(builder.Options);
    }

    public void Dispose() => _connection.Dispose();
}
