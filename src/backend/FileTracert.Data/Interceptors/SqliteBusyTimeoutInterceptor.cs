using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FileTracert.Data.Interceptors;

/// <summary>
/// Sets <c>PRAGMA busy_timeout</c> on every opened connection. SQLite serialises writers even
/// in WAL mode; without a busy timeout a concurrent writer (scan bulk-insert vs. background
/// worker vs. API) fails immediately with "database is locked". A few seconds of wait lets the
/// current writer finish and the second one proceed instead of throwing.
/// </summary>
public sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    private const int BusyTimeoutMs = 5_000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ApplyBusyTimeout(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ApplyBusyTimeout(connection);
        return Task.CompletedTask;
    }

    private static void ApplyBusyTimeout(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};";
        cmd.ExecuteNonQuery();
    }
}
