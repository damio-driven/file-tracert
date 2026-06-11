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
}
