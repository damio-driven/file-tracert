namespace FileTracert.Data.Entities;

/// <summary>Singleton typed settings (always Id = 1).</summary>
public class AppSettings : IAuditable
{
    public int Id { get; set; }

    /// <summary>Default extension allow-list (JSON column).</summary>
    public List<string> DefaultExtensionFilter { get; set; } = new();

    /// <summary>Excluded path fragments (JSON column).</summary>
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>Loopback API token, generated at startup.</summary>
    public string ApiToken { get; set; } = null!;

    /// <summary>Hard-check space margin percentage before execution.</summary>
    public int SpaceMarginPercent { get; set; }

    /// <summary>Minimum log level name (Trace…None); applied to the runtime switch at startup.</summary>
    public string MinimumLogLevel { get; set; } = "Information";

    /// <summary>Log retention horizon in days; entries older than this are trimmed.</summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>Hard cap on log rows kept; the oldest beyond this are trimmed.</summary>
    public int LogMaxRows { get; set; } = 500_000;

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
