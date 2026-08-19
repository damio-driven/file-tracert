namespace FileTracert.Contracts.Platform;

/// <summary>
/// Port toward the OS for discovering volumes and their stable identity.
/// Implemented in the Platform layer (Win32). No file scanning here.
/// </summary>
public interface IVolumeProbe
{
    /// <summary>
    /// All mountable volumes present on the system right now, including
    /// partitions without a drive letter.
    /// </summary>
    IReadOnlyList<ProbedVolume> EnumerateVolumes();

    /// <summary>
    /// A single volume by its GUID path, or null when not currently present.
    /// </summary>
    ProbedVolume? TryGetByGuid(string volumeGuid);

    /// <summary>
    /// Free bytes on the volume RIGHT NOW, or null when the volume cannot be reached
    /// (unmounted, removed, not ready). Deliberately narrower than
    /// <see cref="TryGetByGuid"/>: that one has to enumerate every volume on the system and
    /// resolve physical-disk topology to answer, which is far too much work for the one number
    /// the execution-time space re-check needs before every cross-volume job. A null answer is
    /// not an error to throw on — it is the volume telling us it is not there.
    /// </summary>
    long? TryGetFreeBytes(string volumeGuid);
}
