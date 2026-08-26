namespace FileTracert.Contracts.Enums;

public enum JobType
{
    CreateFolder,
    RenameFile,
    RenameFolder,
    MoveFile,
    MoveFolder,

    /// <summary>
    /// Copy one file to a destination directory. Two types instead of one generic <c>Copy</c>
    /// (step 15a): §5 lists the queueable operations PER ENTITY and every exhaustive switch in the
    /// backend is written that way — a single type would force each branch to re-derive "file or
    /// folder?" from optional request fields.
    /// </summary>
    CopyFile,

    /// <summary>Copy a folder — and, cross-volume, its whole indexed subtree — to a destination.</summary>
    CopyFolder
}
