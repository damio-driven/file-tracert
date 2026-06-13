namespace FileTracert.Contracts.Dtos;

/// <summary>Create a monitored root under a volume. <c>FilterOverride</c> null = use the global default.</summary>
public sealed record CreateWatchedRootRequest(string RelativePath, FilterOverrideDto? FilterOverride);

/// <summary>
/// Patch a monitored root. Null fields are left unchanged (partial update).
/// Setting <c>FilterOverride</c> to a value with <c>UseDefault=true</c> clears the override.
/// </summary>
public sealed record UpdateWatchedRootRequest(bool? IsActive, FilterOverrideDto? FilterOverride);

/// <summary>
/// Per-root filter override. <c>UseDefault=true</c> means "fall back to the global
/// default" (clears any stored override); otherwise <c>Extensions</c> is the explicit allow-list.
/// </summary>
public sealed record FilterOverrideDto(bool UseDefault, IReadOnlyList<string> Extensions);
