namespace FileTracert.Host.Configuration;

/// <summary>
/// Typed host configuration bound from the <c>FileTracert</c> section of
/// appsettings (plus env/CLI overrides). All values have sensible defaults so
/// the service runs out of the box.
/// </summary>
public sealed class FileTracertOptions
{
    public const string SectionName = "FileTracert";

    /// <summary>Fixed loopback port for the Web API.</summary>
    public int Port { get; set; } = 5005;

    /// <summary>
    /// Explicit database file path. When null the host uses
    /// <c>%LOCALAPPDATA%\FileTracert\filetracert.db</c>. Mainly an override for tests.
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>Header carrying the loopback API token.</summary>
    public string TokenHeader { get; set; } = "X-FileTracert-Token";

    /// <summary>How often <see cref="Workers.VolumeSyncWorker"/> reconciles volumes.</summary>
    public int VolumeSyncIntervalSeconds { get; set; } = 60;

    /// <summary>How often <see cref="Workers.ScanWorker"/> looks for volumes still needing a full scan.</summary>
    public int ScanPollIntervalSeconds { get; set; } = 30;

    /// <summary>Graceful shutdown budget for in-flight workers.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 30;

    /// <summary>How often <see cref="Workers.LogRetentionWorker"/> trims the log database.</summary>
    public int LogRetentionIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// How often <see cref="Workers.WalCheckpointWorker"/> merges + truncates the WAL of the
    /// main and log databases. Under constant read/write traffic SQLite's passive
    /// auto-checkpoint can starve, letting the WAL grow unbounded (observed: 555 MB) which
    /// slows every write and eventually causes "database is locked".
    /// </summary>
    public int WalCheckpointIntervalSeconds { get; set; } = 120;
}
