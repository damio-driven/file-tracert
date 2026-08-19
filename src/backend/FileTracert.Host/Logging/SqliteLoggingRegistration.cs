using FileTracert.Contracts.Logging;
using Microsoft.Extensions.Logging;

namespace FileTracert.Host.Logging;

/// <summary>
/// Wires the dedicated log database into the logging pipeline: queued provider, category
/// policy, runtime level switch and the shutdown drain. One place, so what the host does and
/// what the tests assert cannot drift apart.
/// </summary>
public static class SqliteLoggingRegistration
{
    /// <summary>
    /// Registers the queued SQLite sink. The store must already have its schema: the sink is
    /// built before the container exists, because everything after this line may log.
    /// </summary>
    public static void AddSqliteLogging(
        this IHostApplicationBuilder builder,
        ILogStore store,
        LogLevelSwitch levelSwitch,
        TimeSpan? drainTimeout = null)
    {
        var processor = new SqliteLogProcessor(store, drainTimeout: drainTimeout);

        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(levelSwitch);
        builder.Services.AddSingleton(processor);

        // First registration = last to stop: the queue closes after every other worker has
        // logged its shutdown. See LogFlushService for why this cannot be left to the container.
        builder.Services.AddHostedService<LogFlushService>();

        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        // Category-aware gate: the user switch governs FileTracert categories; framework
        // categories (Microsoft.*, System.*) stay capped at Warning — EF internals at Debug
        // once flooded the log DB (~1M rows/hour) until main-DB writes timed out.
        builder.Logging.AddFilter((category, level) =>
            LogCategoryPolicy.IsEnabled(category ?? string.Empty, level, levelSwitch.Current));
        // The console sink (added by the default host builder) stays alongside it, so
        // early-startup logs — emitted before the log DB is ready — are never lost.
        builder.Logging.AddProvider(new SqliteLoggerProvider(processor, levelSwitch));
    }
}
