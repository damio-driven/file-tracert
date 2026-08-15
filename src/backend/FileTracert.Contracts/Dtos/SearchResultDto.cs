using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Dtos;

/// <param name="Name">Projected name (<c>PendingName ?? Name</c>) — §5.</param>
/// <param name="RelativePath">
/// PROJECTED path: the parents are walked with their overlays applied, so a file under a folder
/// with a queued rename or move is shown where it is going.
/// </param>
/// <param name="VolumeId">
/// PROJECTED volume — the volume of the projected directory. A queued cross-volume move shows
/// the destination immediately; the row itself changes volume only at execution.
/// </param>
/// <param name="ProjectedState">
/// <c>EntityPendingState</c> as a string: <c>None</c> when nothing is queued on this file.
/// </param>
/// <param name="PendingJobId">The queued job the badge belongs to, so the UI can link to it.</param>
public sealed record SearchResultDto(
    int FileId,
    string Name,
    string RelativePath,
    int VolumeId,
    string? VolumeLabel,
    string? VolumeLetter,
    bool VolumeIsOnline,
    long SizeBytes,
    DateTime ModifiedUtc,
    FileCategory Category,
    string ProjectedState,
    int? PendingJobId);
