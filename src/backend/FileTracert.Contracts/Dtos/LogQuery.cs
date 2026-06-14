namespace FileTracert.Contracts.Dtos;

/// <summary>
/// Filter + paging shape for querying the log store. <paramref name="MinLevel"/> is
/// the integer level floor (rows at or above it are returned); the string filters
/// are substring/equality matches resolved by the store.
/// </summary>
public sealed record LogQuery(
    int Skip,
    int Take,
    int? MinLevel = null,
    string? Category = null,
    string? Search = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null);
