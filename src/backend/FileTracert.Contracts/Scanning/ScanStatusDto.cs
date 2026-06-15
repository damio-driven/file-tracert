using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Scanning;

/// <summary>
/// Live progress snapshot of one volume being scanned. Volatile (in-memory only);
/// polled today via <c>GET /api/scans/status</c> and pushed via SignalR at step 10
/// with the same shape.
/// </summary>
public sealed record ScanStatusDto(
    int VolumeId,
    string? Label,
    ScanPhase Phase,
    long ItemsSeen,
    long ItemsWritten,
    string? CurrentRoot,
    DateTime StartedUtc,
    DateTime UpdatedUtc);
