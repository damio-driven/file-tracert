using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Dtos;

public sealed record SearchResultDto(
    int FileId,
    string Name,
    string RelativePath,
    int VolumeId,
    string? VolumeLabel,
    string? VolumeLetter,
    bool VolumeIsOnline,
    long SizeBytes,
    DateTime ModifiedUtc,
    FileCategory Category,
    string ProjectedState);   // placeholder "None" until step 9
