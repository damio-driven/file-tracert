namespace FileTracert.Contracts.Paging;

/// <summary>
/// One page of results plus the total count, so the UI can render paging without
/// a second round-trip. <paramref name="Skip"/>/<paramref name="Take"/> echo the
/// (normalized) request that produced this page.
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Skip,
    int Take);
