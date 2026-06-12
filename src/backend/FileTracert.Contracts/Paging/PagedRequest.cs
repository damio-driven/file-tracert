namespace FileTracert.Contracts.Paging;

/// <summary>
/// Shared server-side paging/sort contract. Stated here once so the heavy lists
/// (Catalogo, Ricerca — millions of rows, step 7) all speak the same shape; the
/// few volumes don't need it but reuse the same vocabulary.
/// </summary>
/// <param name="Skip">Rows to skip (offset). Negative values are clamped to 0.</param>
/// <param name="Take">Page size requested. Capped by <see cref="Normalized"/>.</param>
/// <param name="SortBy">Optional sort key, interpreted per endpoint.</param>
/// <param name="Desc">Sort descending when true.</param>
public sealed record PagedRequest(int Skip, int Take, string? SortBy = null, bool Desc = false)
{
    /// <summary>Hard cap on <see cref="Take"/> so a client can't ask for an absurd page.</summary>
    public const int MaxTake = 200;

    /// <summary>Default page size when a caller passes a non-positive <see cref="Take"/>.</summary>
    public const int DefaultTake = 50;

    /// <summary>
    /// Returns a sane copy: non-negative <see cref="Skip"/>, and <see cref="Take"/>
    /// coerced into <c>[1, maxTake]</c> (a non-positive Take falls back to <see cref="DefaultTake"/>).
    /// </summary>
    public PagedRequest Normalized(int maxTake = MaxTake)
    {
        var skip = Skip < 0 ? 0 : Skip;
        var take = Take <= 0 ? DefaultTake : Take;
        if (take > maxTake)
        {
            take = maxTake;
        }

        return this with { Skip = skip, Take = take };
    }
}
