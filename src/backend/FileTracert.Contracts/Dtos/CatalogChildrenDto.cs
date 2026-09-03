using FileTracert.Contracts.Paging;

namespace FileTracert.Contracts.Dtos;

public sealed record CatalogChildrenDto(
    PagedResult<CatalogDirDto> Directories,
    PagedResult<CatalogFileDto> Files,
    bool VolumeIsOnline,
    string? VolumeLabel,
    string? VolumeLetter,
    int? CurrentDirectoryId,
    string? CurrentDirectoryPath);
