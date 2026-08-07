namespace FileTracert.HardwareSmoke;

/// <summary>What kind of device a configured test volume lives on. Only used to describe the
/// generated source→target pairs in the report and to gate the unplug prompts, which only make
/// sense for a removable drive.</summary>
public enum TestVolumeKind
{
    Internal,
    External,
}

/// <summary>One sacrificable area the harness may use, on a real volume.</summary>
public sealed class TestVolumeOptions
{
    /// <summary>Short label used in the report (e.g. <c>internal-a</c>). Must be unique.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// An existing folder the harness may write into. The harness never touches its existing
    /// content: everything it creates lives under <see cref="HardwareSmokeOptions.ScratchSubfolder"/>
    /// inside this folder.
    /// </summary>
    public string Path { get; set; } = "";

    public TestVolumeKind Kind { get; set; } = TestVolumeKind.Internal;
}

/// <summary>
/// Opt-in configuration for the hardware harness, bound from the <c>HardwareSmoke</c> section of
/// <c>appsettings.json</c>. The harness does NOTHING unless <see cref="Enabled"/> is true and at
/// least one usable <see cref="TestVolumes"/> entry is configured — the user must switch it on
/// deliberately and point it at folders they are willing to lose.
/// </summary>
public sealed class HardwareSmokeOptions
{
    public const string SectionName = "HardwareSmoke";

    /// <summary>Master switch. Default false: a fresh checkout never touches the disk.</summary>
    public bool Enabled { get; set; }

    /// <summary>The areas the scenarios may operate in, ideally one per physical drive.</summary>
    public List<TestVolumeOptions> TestVolumes { get; set; } = [];

    /// <summary>
    /// Single folder name (no separators) created inside every test volume path. All fixtures
    /// live under it and it is the ONLY thing the cleanup removes.
    /// </summary>
    public string ScratchSubfolder { get; set; } = "FileTracertHarness";

    /// <summary>
    /// Scenario name filter. <c>["*"]</c> (or empty) runs everything; otherwise only scenarios
    /// whose name matches one of the entries case-insensitively.
    /// Starts EMPTY on purpose: the configuration binder <em>appends</em> to a pre-populated list
    /// instead of replacing it, so a default of <c>["*"]</c> would silently win over whatever the
    /// operator configured and always run everything.
    /// </summary>
    public List<string> Scenarios { get; set; } = [];

    /// <summary>
    /// Enables the interactive scenarios that ask the operator to physically unplug and replug a
    /// drive. Off by default so an unattended run never blocks on a console prompt.
    /// </summary>
    public bool SemiAutomatic { get; set; }

    /// <summary>
    /// Override for the production database the guard reads WatchedRoots from. Empty = the
    /// service's own convention (<c>%LOCALAPPDATA%\FileTracert\filetracert.db</c>).
    /// </summary>
    public string? MainDatabasePath { get; set; }

    /// <summary>
    /// Size of the fixture file used by the timing-sensitive scenarios (cancel and crash/resume):
    /// it must take long enough to copy that the harness can interrupt it mid-flight. Raise it on
    /// fast NVMe drives if those scenarios report "completed before it could be interrupted".
    /// </summary>
    public int LargeFileMegabytes { get; set; } = 96;

    /// <summary>Upper bound on how long a single scenario may run before it is failed as stuck.</summary>
    public int ScenarioTimeoutSeconds { get; set; } = 180;
}
