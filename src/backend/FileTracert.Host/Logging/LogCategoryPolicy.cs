using Microsoft.Extensions.Logging;

namespace FileTracert.Host.Logging;

/// <summary>
/// Decides whether a log entry is enabled given its category, its level and the
/// user-facing minimum level (the runtime <see cref="LogLevelSwitch"/>).
///
/// The user switch governs FileTracert categories only. Framework categories
/// (Microsoft.*, System.*) are capped at Warning regardless: at Debug, EF Core's
/// ChangeTracking logs one entry per tracked entity — a scan produced ~1M rows/hour
/// in the log DB and the resulting I/O saturation made main-DB writes time out
/// ("database is locked").
/// </summary>
public static class LogCategoryPolicy
{
    public static bool IsEnabled(string category, LogLevel level, LogLevel userMinimum)
    {
        if (level == LogLevel.None)
        {
            return false;
        }

        if (IsFrameworkCategory(category))
        {
            return level >= LogLevel.Warning;
        }

        return level >= userMinimum;
    }

    private static bool IsFrameworkCategory(string category) =>
        category.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        category.StartsWith("System.", StringComparison.Ordinal);
}
