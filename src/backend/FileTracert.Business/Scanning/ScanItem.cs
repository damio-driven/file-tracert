namespace FileTracert.Business.Scanning;

/// <summary>
/// Engine-agnostic scan record flowing through the pipeline. USN entries carry
/// an <see cref="Frn"/> but no size/timestamps; enumeration entries carry
/// size/timestamps but no FRN.
/// </summary>
internal sealed record ScanItem(
    string RelativePath,
    string Name,
    bool IsDirectory,
    long? SizeBytes,
    DateTime? CreatedUtc,
    DateTime? ModifiedUtc,
    FileAttributes Attributes,
    ulong? Frn);
