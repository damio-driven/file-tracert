namespace FileTracert.HardwareSmoke;

/// <summary>
/// Opt-in configuration for the hardware-smoke harness, bound from the <c>HardwareSmoke</c>
/// section of <c>appsettings.json</c>. The harness does NOTHING unless <see cref="Enabled"/> is
/// true and all three paths are set — the user must switch it on deliberately.
/// </summary>
public sealed class HardwareSmokeOptions
{
    public const string SectionName = "HardwareSmoke";

    /// <summary>Master switch. Default false: a fresh checkout never touches the disk.</summary>
    public bool Enabled { get; set; }

    /// <summary>A real, sacrificable folder holding files the harness may duplicate and move.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Target folder for cross-volume moves (ideally on another disk).</summary>
    public string TargetPath { get; set; } = "";

    /// <summary>Working area where the harness duplicates the source files and operates on the copies.</summary>
    public string ScratchPath { get; set; } = "";
}
