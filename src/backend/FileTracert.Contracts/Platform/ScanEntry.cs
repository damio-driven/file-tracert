namespace FileTracert.Contracts.Platform;

/// <summary>
/// One filesystem object from the BCL enumeration fallback (exFAT/FAT32, or any
/// volume without a usable journal). Unlike <see cref="UsnEntry"/>, size and
/// timestamps are populated directly from <c>FileInfo</c>.
/// </summary>
/// <param name="RelativePath">Path relative to the volume root (no leading separator).</param>
/// <param name="Name">File or directory name (no path).</param>
/// <param name="IsDirectory">True when the entry is a directory.</param>
/// <param name="SizeBytes">File size in bytes; 0 for directories.</param>
/// <param name="CreatedUtc">Creation time (UTC).</param>
/// <param name="ModifiedUtc">Last write time (UTC).</param>
/// <param name="Attributes">Win32 file attributes.</param>
/// <param name="Frn">
/// The object's file reference number, when the filesystem gives it one — the same identity the
/// change journal speaks in. Null on filesystems that have none, and on entries whose id could not
/// be read. Whether it is trustworthy is the caller's call: this layer reports what it read.
/// </param>
public sealed record ScanEntry(
    string RelativePath,
    string Name,
    bool IsDirectory,
    long SizeBytes,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    FileAttributes Attributes,
    ulong? Frn = null);
