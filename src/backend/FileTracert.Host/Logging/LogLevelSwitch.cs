using Microsoft.Extensions.Logging;

namespace FileTracert.Host.Logging;

/// <summary>
/// Mutable, thread-safe minimum log level consulted by both the global logging
/// filter and the SQLite logger. Changing <see cref="Current"/> takes effect
/// immediately, no restart. Persistence to <c>AppSettings</c> happens separately.
/// </summary>
public sealed class LogLevelSwitch
{
    private volatile LogLevel _current;

    public LogLevelSwitch(LogLevel initial = LogLevel.Information) => _current = initial;

    public LogLevel Current
    {
        get => _current;
        set => _current = value;
    }
}
