using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Search;

public sealed record FileSearchQuery(
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

public enum SearchScope { Name, FullPath }
public enum SearchSort { Relevance, Name, Date, Size }
