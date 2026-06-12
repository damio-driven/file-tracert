namespace FileTracert.Contracts.Platform;

/// <summary>
/// A single incremental change read from the journal: the affected entry plus
/// why it changed.
/// </summary>
/// <param name="Entry">The affected filesystem object.</param>
/// <param name="Reason">OR-ed change reasons for this record.</param>
/// <param name="IsRename">True when this record is part of a rename (old or new name).</param>
/// <param name="OldName">
/// Previous name, when known (carried on the <see cref="UsnReason.RenameOldName"/> record).
/// </param>
public sealed record UsnChangeRecord(
    UsnEntry Entry,
    UsnReason Reason,
    bool IsRename,
    string? OldName);
