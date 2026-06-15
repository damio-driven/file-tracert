using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Scanning;

/// <summary>
/// Thread-safe, in-memory tracker of full-scan progress. The scan pipeline updates
/// it at phase boundaries and periodically during the long enumeration; the API
/// reads <see cref="Snapshot"/> to answer progress polls. State is effimeral — an
/// entry exists only while its volume is actively being scanned.
/// </summary>
public interface IScanStatusTracker
{
    /// <summary>Begin tracking a volume's scan (phase = Enumerating, counters reset).</summary>
    void Begin(int volumeId, string? label);

    /// <summary>Move the volume's scan to a new phase.</summary>
    void SetPhase(int volumeId, ScanPhase phase);

    /// <summary>Report the running count of items seen during enumeration.</summary>
    void ReportSeen(int volumeId, long itemsSeen, string? currentRoot = null);

    /// <summary>Report the count of items written to the index.</summary>
    void ReportWritten(int volumeId, long itemsWritten);

    /// <summary>Stop tracking a volume whose scan finished successfully.</summary>
    void Complete(int volumeId);

    /// <summary>Stop tracking a volume whose scan failed.</summary>
    void Fail(int volumeId);

    /// <summary>All scans currently in flight (empty when none).</summary>
    IReadOnlyList<ScanStatusDto> Snapshot();
}
