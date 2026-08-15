namespace FileTracert.Contracts.Dtos;

/// <param name="Name">Projected name (<c>PendingName ?? Name</c>) — §5.</param>
/// <param name="MaterializedPath">
/// PHYSICAL path, not the projected one: it is the row's identity and what an operation targets.
/// </param>
/// <param name="ProjectedState">
/// <c>EntityPendingState</c> as a string: <c>None</c> when nothing is queued on this folder,
/// otherwise the badge the UI shows.
/// </param>
/// <param name="PendingJobId">The queued job the badge belongs to, so the UI can link to it.</param>
public sealed record CatalogDirDto(
    int Id,
    string Name,
    string MaterializedPath,
    int ChildDirectoryCount,
    int FileCount,
    string ProjectedState,
    int? PendingJobId);
