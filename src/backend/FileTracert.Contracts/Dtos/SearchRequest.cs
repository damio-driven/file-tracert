using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Search;

namespace FileTracert.Contracts.Dtos;

/// <summary>Request body for POST /api/search.</summary>
public sealed record SearchRequest(
    string Text,
    SearchScope Scope,
    FileCategory? Category,
    string[]? Extensions,
    long? SizeBytesMin,
    long? SizeBytesMax,
    DateTime? ModifiedFrom,
    DateTime? ModifiedTo,
    int? VolumeId,
    bool OnlineOnly,
    SearchSort Sort,
    bool Desc,
    int Skip,
    int Take)
{
    /// <summary>
    /// Builds a <see cref="FileSearchQuery"/> from this request using the supplied
    /// normalised paging values (use <see cref="Paging.PagedRequest.Normalized"/> before calling).
    /// </summary>
    public FileSearchQuery ToQuery(int skip, int take) =>
        new(Text, Scope, Category, Extensions,
            SizeBytesMin, SizeBytesMax,
            ModifiedFrom, ModifiedTo,
            VolumeId, OnlineOnly,
            Sort, Desc, skip, take);
}
