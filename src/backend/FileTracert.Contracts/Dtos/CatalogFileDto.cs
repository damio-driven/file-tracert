using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Dtos;

public sealed record CatalogFileDto(
    int Id,
    string Name,
    long SizeBytes,
    DateTime ModifiedUtc,
    FileCategory Category,
    string ProjectedState);   // placeholder "None" until step 9
