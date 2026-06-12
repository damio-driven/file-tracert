namespace FileTracert.Contracts.Platform;

/// <summary>
/// One filesystem object as seen through the USN/MFT, with its path already
/// reconstructed relative to the volume root.
/// </summary>
/// <param name="FileReferenceNumber">MFT file reference number (the FRN) — stable per-volume id.</param>
/// <param name="ParentFileReferenceNumber">FRN of the containing directory.</param>
/// <param name="Name">File or directory name (no path).</param>
/// <param name="RelativePath">Path relative to the volume root (no leading separator).</param>
/// <param name="IsDirectory">True when the entry is a directory.</param>
/// <param name="SizeBytes">
/// File size in bytes, or null. USN records do not carry size, so the full MFT
/// snapshot leaves this null by design (Business fills it lazily/batched);
/// the directory-enumeration fallback populates it directly.
/// </param>
/// <param name="Attributes">Win32 file attributes.</param>
/// <param name="Usn">The USN of the record this entry was read from.</param>
public sealed record UsnEntry(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    string Name,
    string RelativePath,
    bool IsDirectory,
    long? SizeBytes,
    FileAttributes Attributes,
    long Usn);
