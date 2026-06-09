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

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
