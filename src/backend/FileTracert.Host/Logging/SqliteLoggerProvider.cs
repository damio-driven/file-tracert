using Microsoft.Extensions.Logging;

namespace FileTracert.Host.Logging;

/// <summary>
/// Registers the SQLite sink as a logging provider. The console sink stays active
/// alongside it (added by the default host builder) so early-startup logs — emitted
/// before the log DB is ready — are never lost. The processor's lifecycle is owned
/// by DI (singleton), so disposing the provider must not tear it down.
/// </summary>
[ProviderAlias("Sqlite")]
public sealed class SqliteLoggerProvider : ILoggerProvider
{
    private readonly SqliteLogProcessor _processor;
    private readonly LogLevelSwitch _levelSwitch;

    public SqliteLoggerProvider(SqliteLogProcessor processor, LogLevelSwitch levelSwitch)
    {
        _processor = processor;
        _levelSwitch = levelSwitch;
    }

    public ILogger CreateLogger(string categoryName) =>
        new SqliteLogger(categoryName, _processor, _levelSwitch);

    public void Dispose()
    {
        // The processor is a DI-owned singleton; it is flushed/disposed there.
    }
}
