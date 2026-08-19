using Microsoft.Extensions.Logging;

namespace FileTracert.Host.Logging;

/// <summary>
/// Registers the SQLite sink as a logging provider. The console sink stays active
/// alongside it (added by the default host builder) so early-startup logs — emitted
/// before the log DB is ready — are never lost.
/// <para>
/// Disposing the provider deliberately does <em>not</em> tear the processor down: the
/// provider is one of several holders of a queue that must stay open until every worker has
/// logged its last line. Closing and draining it is <see cref="LogFlushService"/>'s job, at
/// the end of the host's stop sequence.
/// </para>
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
        // Intentionally empty: see the note above — LogFlushService owns the drain.
    }
}
