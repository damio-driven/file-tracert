namespace FileTracert.HardwareSmoke;

/// <summary>
/// A configured test area after it has been resolved against the live system: which physical
/// volume it sits on (the GUID path the <c>IFileMover</c> speaks) and where its scratch area is,
/// both as an absolute path and as the volume-relative path the queue stores in the DB.
/// </summary>
/// <param name="Name">Config name, used in the report.</param>
/// <param name="Kind">Internal/External, from the config.</param>
/// <param name="ConfiguredPath">The folder the user pointed at (absolute).</param>
/// <param name="VolumeGuid">Volume GUID path (<c>\\?\Volume{GUID}\</c>) — the identity everything keys on.</param>
/// <param name="MountPoint">Current mount of that volume (e.g. <c>E:\</c>).</param>
/// <param name="ScratchFullPath">Absolute path of the harness-owned scratch area.</param>
/// <param name="ScratchRelativePath">Same area, relative to the volume root (DB/queue form).</param>
public sealed record TestVolume(
    string Name,
    TestVolumeKind Kind,
    string ConfiguredPath,
    string VolumeGuid,
    string MountPoint,
    string ScratchFullPath,
    string ScratchRelativePath);
