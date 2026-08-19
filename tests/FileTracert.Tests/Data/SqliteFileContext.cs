using FileTracert.Data;
using FileTracert.Data.Interceptors;
using FileTracert.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

/// <summary>
/// A real SQLite <em>file</em> database in a temp folder, in WAL mode. Unlike
/// <see cref="SqliteInMemoryContext"/> — which shares one connection, so writers never
/// actually contend — each context here opens its own connection, which is the only way to
/// observe write-lock behaviour (SQLITE_BUSY, cross-connection visibility of a commit).
/// </summary>
public sealed class SqliteFileContext : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly int _busyTimeoutMs;

    static SqliteFileContext()
    {
        SQLitePCL.Batteries.Init();
    }

    /// <param name="busyTimeoutMs">Deliberately short by default: a blocked writer must fail
    /// fast in tests instead of waiting out the production 15 s budget.</param>
    public SqliteFileContext(int busyTimeoutMs = 250)
    {
        _busyTimeoutMs = busyTimeoutMs;
        _dir = Path.Combine(Path.GetTempPath(), "filetracert-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "filetracert.db");

        using var context = CreateContext();
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    public FileTracertDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<FileTracertDbContext>()
            .UseSqlite(new SqliteConnection(SqliteTestDatabase.ConnectionString(_path)))
            .AddInterceptors(new AuditingSaveChangesInterceptor(), new ShortBusyTimeoutInterceptor(_busyTimeoutMs));

        return new FileTracertDbContext(builder.Options);
    }

    /// <summary>
    /// Releases only this context's own pool — clearing every pool in the process would
    /// dispose the native handle of whatever another test class is querying (see
    /// <see cref="SqliteTestDatabase"/>).
    /// </summary>
    public void Dispose() => SqliteTestDatabase.DeleteDirectory(_dir, _path);

    /// <summary>Same job as <see cref="SqliteBusyTimeoutInterceptor"/>, with a test-sized budget.</summary>
    private sealed class ShortBusyTimeoutInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
    {
        private readonly int _timeoutMs;

        public ShortBusyTimeoutInterceptor(int timeoutMs) => _timeoutMs = timeoutMs;

        public override void ConnectionOpened(
            System.Data.Common.DbConnection connection,
            Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData)
            => Apply(connection);

        public override Task ConnectionOpenedAsync(
            System.Data.Common.DbConnection connection,
            Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Apply(connection);
            return Task.CompletedTask;
        }

        private void Apply(System.Data.Common.DbConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA busy_timeout={_timeoutMs};";
            cmd.ExecuteNonQuery();
        }
    }
}
