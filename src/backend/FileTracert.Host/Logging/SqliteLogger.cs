using FileTracert.Contracts.Logging;
using Microsoft.Extensions.Logging;

namespace FileTracert.Host.Logging;

/// <summary>
/// <see cref="ILogger"/> that captures each enabled entry — including the full
/// formatted exception (message + stack + inner) — and hands it to the
/// <see cref="SqliteLogProcessor"/>. Gated by the runtime <see cref="LogLevelSwitch"/>.
/// </summary>
internal sealed class SqliteLogger : ILogger
{
    private readonly string _category;
    private readonly SqliteLogProcessor _processor;
    private readonly LogLevelSwitch _levelSwitch;

    public SqliteLogger(string category, SqliteLogProcessor processor, LogLevelSwitch levelSwitch)
    {
        _category = category;
        _processor = processor;
        _levelSwitch = levelSwitch;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        LogCategoryPolicy.IsEnabled(_category, logLevel, _levelSwitch.Current);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var record = new LogRecord(
            TimestampUtc: DateTime.UtcNow,
            Level: (int)logLevel,
            Category: _category,
            Message: message,
            Exception: exception?.ToString(),
            EventId: eventId.Id == 0 ? null : eventId.Id,
            Scope: null);

        _processor.Enqueue(record);
    }
}
