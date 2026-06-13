namespace FileTracert.Contracts.Dtos;

/// <summary>One immediate sub-folder, as returned by the browse endpoint.</summary>
public sealed record FolderNodeDto(string Name, string RelativePath, bool HasChildren);
