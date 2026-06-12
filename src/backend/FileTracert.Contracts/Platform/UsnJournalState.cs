namespace FileTracert.Contracts.Platform;

/// <summary>
/// Snapshot of the volume's USN change journal (from <c>FSCTL_QUERY_USN_JOURNAL</c>).
/// </summary>
/// <param name="JournalId">Journal instance id. Changes if the journal is deleted/recreated.</param>
/// <param name="FirstUsn">First USN still recorded in the journal.</param>
/// <param name="NextUsn">USN that will be assigned to the next record.</param>
/// <param name="LowestValidUsn">Lowest USN that can still be safely read; below it the data is gone.</param>
public sealed record UsnJournalState(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn);
