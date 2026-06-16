namespace FileTracert.Contracts.Dtos;

public sealed record CatalogDirDto(
    int Id,
    string Name,
    string MaterializedPath,
    int ChildDirectoryCount,
    int FileCount);
