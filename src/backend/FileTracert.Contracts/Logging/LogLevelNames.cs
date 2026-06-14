namespace FileTracert.Contracts.Logging;

/// <summary>
/// Maps the integer log level (matching <c>Microsoft.Extensions.Logging.LogLevel</c>:
/// Trace=0 … Critical=5, None=6) to/from its canonical name, without taking a
/// dependency on the logging abstractions from <c>Contracts</c>.
/// </summary>
public static class LogLevelNames
{
    private static readonly string[] Names =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    /// <summary>Name for a level value; out-of-range values fall back to the raw number.</summary>
    public static string ToName(int level) =>
        level >= 0 && level < Names.Length ? Names[level] : level.ToString();

    /// <summary>
    /// Parses a level name (case-insensitive) to its integer value, or <c>null</c>
    /// when unrecognized.
    /// </summary>
    public static int? TryParse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        for (var i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }
}
