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
    int Take);
