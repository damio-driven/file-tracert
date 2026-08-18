namespace FileTracert.Contracts.Platform;

/// <summary>
/// Port toward the OS device-notification machinery: tells the host that storage
/// hardware appeared or disappeared, so a volume re-probe can happen at once
/// instead of waiting for the periodic sync.
/// <para>
/// Deliberately identity-free. The OS notification carries a device-interface
/// <em>symbolic link name</em> (<c>\?\STORAGE#Volume#…</c>), which is not the Volume
/// GUID path this application keys on (CLAUDE.md §4) and cannot be mapped to it
/// cheaply or reliably. The event therefore means only "something changed, re-probe";
/// resolving identity stays the job of the volume sync, which enumerates and matches
/// by GUID as it already does.
/// </para>
/// </summary>
public interface IDeviceWatcher : IDisposable
{
    /// <summary>
    /// Raised on an OS thread, possibly as a burst for a single physical insertion.
    /// Handlers must return immediately; coalescing is the consumer's job.
    /// </summary>
    event EventHandler<DeviceChangeEvent>? Changed;

    /// <summary>
    /// Registers with the OS. Throws when registration fails — the caller decides
    /// whether that is fatal (it is not: the periodic sync is the safety net).
    /// Calling it twice is a no-op after the first success.
    /// </summary>
    void Start();
}

/// <summary>A storage device change, without any device identity (see <see cref="IDeviceWatcher"/>).</summary>
public sealed record DeviceChangeEvent(DeviceChangeKind Kind, DateTime TimestampUtc);

/// <summary>Direction of a <see cref="DeviceChangeEvent"/>.</summary>
public enum DeviceChangeKind
{
    /// <summary>A storage volume interface became available.</summary>
    Arrived,

    /// <summary>A storage volume interface went away.</summary>
    Removed,
}
