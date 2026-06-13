namespace FileTracert.Contracts.Dtos;

/// <summary>
/// Global default file-type filter (AppSettings). <c>AllowedExtensions</c> empty = all types.
/// <c>ExcludedPaths</c> are path segments excluded everywhere (Windows, AppData, …).
/// </summary>
public sealed record FilterSettingsDto(
    IReadOnlyList<string> AllowedExtensions,
    IReadOnlyList<string> ExcludedPaths);
