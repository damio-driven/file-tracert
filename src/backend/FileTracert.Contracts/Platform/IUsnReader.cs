namespace FileTracert.Contracts.Platform;

/// <summary>
/// Port toward the NTFS USN change journal. Implemented in Platform.
/// Business picks this engine only when <see cref="SupportsUsn"/> is true.
/// </summary>
public interface IUsnReader
{
    /// <summary>True when the volume is NTFS and a usable change journal is present/creatable.</summary>
    bool SupportsUsn(string volumeGuid);

    /// <summary>Current journal state (<c>FSCTL_QUERY_USN_JOURNAL</c>).</summary>
    UsnJournalState GetJournalState(string volumeGuid);

    /// <summary>Creates the journal if absent. Requires administrative privileges.</summary>
    void EnsureJournal(string volumeGuid);

    /// <summary>
    /// Full MFT enumeration. Yields raw records with the relative path already
    /// reconstructed. <see cref="UsnEntry.SizeBytes"/> is left null (USN has no size).
    /// </summary>
    IEnumerable<UsnEntry> ReadFullSnapshot(string volumeGuid, CancellationToken ct);

    /// <summary>
    /// Incremental read from <paramref name="sinceUsn"/>. Signals when the
    /// journal is invalidated (<see cref="UsnChangeResult.RequiresFullRescan"/>).
    /// </summary>
    UsnChangeResult ReadChanges(string volumeGuid, long sinceUsn, ulong journalId, CancellationToken ct);
}
