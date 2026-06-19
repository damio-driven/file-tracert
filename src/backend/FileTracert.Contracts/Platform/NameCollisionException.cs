namespace FileTracert.Contracts.Platform;

/// <summary>
/// Thrown by <see cref="IFileMover.FinalizePartial"/> when the target path already exists.
/// The queue processor catches this and transitions the job to Blocked(NameCollision).
/// </summary>
public sealed class NameCollisionException : Exception
{
    public string TargetPath { get; }

    public NameCollisionException(string targetPath)
        : base($"Target already exists: {targetPath}")
    {
        TargetPath = targetPath;
    }
}
