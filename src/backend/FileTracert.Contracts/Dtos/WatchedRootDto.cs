namespace FileTracert.Contracts.Dtos;

/// <summary>
/// A monitored root folder on a volume. <c>EffectiveFilter</c> is the
/// human-readable filter summary (categories/extensions), already resolved for display.
/// </summary>
public sealed record WatchedRootDto(
    int Id,
    string RelativePath,
    bool IsActive,
    string EffectiveFilter);
