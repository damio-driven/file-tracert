namespace FileTracert.Contracts.Enums;

/// <summary>
/// Coarse phase of an in-flight full scan, surfaced to the UI as a progress hint.
/// The enumeration phase is the long "blind" one (building the FRN map before any
/// write), so the tracker reports a running count during it.
/// </summary>
public enum ScanPhase
{
    Enumerating,
    ResolvingPaths,
    ReadingMetadata,
    Writing,
    Done,
    Failed,
}
