using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Dtos;

/// <param name="Name">Projected name (<c>PendingName ?? Name</c>) — §5.</param>
/// <param name="ProjectedState">
/// <c>EntityPendingState</c> as a string: <c>None</c> when nothing is queued on this file,
/// otherwise the badge the UI shows.
/// </param>
/// <param name="PendingJobId">The queued job the badge belongs to, so the UI can link to it.</param>
public sealed record CatalogFileDto(
    int Id,
    string Name,
    long SizeBytes,
    DateTime ModifiedUtc,
    FileCategory Category,
    string ProjectedState,
    int? PendingJobId);
