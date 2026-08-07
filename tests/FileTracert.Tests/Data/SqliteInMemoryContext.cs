using FileTracert.Data;
using FileTracert.Data.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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

    public SqliteInMemoryContext(bool withAuditing = true, bool ensureCreated = true)
    {
        _withAuditing = withAuditing;
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        if (ensureCreated)
        {
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }
    }

    /// <param name="extraInterceptors">Test-specific interceptors (e.g. fault/race injection)
    /// appended after the standard auditing interceptor.</param>
    public FileTracertDbContext CreateContext(params IInterceptor[] extraInterceptors)
    {
        var builder = new DbContextOptionsBuilder<FileTracertDbContext>()
            .UseSqlite(_connection);

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
