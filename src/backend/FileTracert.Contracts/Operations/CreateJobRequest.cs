using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Operations;

public sealed class CreateJobRequest
{
    public JobType Type { get; set; }

    /// <summary>Source file; required for RenameFile, MoveFile and CopyFile.</summary>
    public int? SourceFileId { get; set; }

    /// <summary>Source directory; required for RenameFolder, MoveFolder and CopyFolder.</summary>
    public int? SourceDirectoryId { get; set; }

    /// <summary>
    /// Target volume; required for MoveFile, MoveFolder, CopyFile, CopyFolder and CreateFolder.
    /// </summary>
    public int? TargetVolumeId { get; set; }

    /// <summary>
    /// Destination directory path (relative to volume root) for Move/Copy ops and CreateFolder.
    /// For MoveFile and CopyFile: the destination directory (filename is appended automatically).
    /// For MoveFolder and CopyFolder: the destination parent directory.
    /// For CreateFolder: the full path of the new folder.
    /// </summary>
    public string? TargetRelativePath { get; set; }

    /// <summary>New name (without path); required for RenameFile and RenameFolder.</summary>
    public string? NewName { get; set; }
}
