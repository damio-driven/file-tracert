namespace FileTracert.Platform;

/// <summary>
/// Builds a best-effort map of drive-letter key (<c>C:</c>) to a descriptive
/// physical-disk id. Internal to Platform.
/// </summary>
internal interface IPhysicalDiskResolver
{
    IReadOnlyDictionary<string, string> BuildDriveLetterToDiskMap();
}
