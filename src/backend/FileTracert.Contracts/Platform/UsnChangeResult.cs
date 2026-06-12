namespace FileTracert.Contracts.Platform;

/// <summary>
/// Result of an incremental journal read.
/// </summary>
/// <param name="Changes">Change records since the requested USN.</param>
/// <param name="NextUsn">USN to pass on the next incremental read.</param>
/// <param name="RequiresFullRescan">
/// True when the journal was invalidated (wrap, journal-id mismatch, or the
/// requested USN fell below <c>LowestValidUsn</c>). The caller must do a full
/// rescan rather than trust partial data.
/// </param>
public sealed record UsnChangeResult(
    IReadOnlyList<UsnChangeRecord> Changes,
    long NextUsn,
    bool RequiresFullRescan);
