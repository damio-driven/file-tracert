using FileTracert.Contracts.Platform;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Business;

/// <summary>
/// Platform fake that answers the free-space probe with the catalog's own
/// <c>FreeBytesLastKnown</c>. Substituting the PORT is legitimate — the component under test is
/// the ledger/engine, not the Win32 probe — and echoing the stored value is what keeps every
/// test that is NOT about the live probe expressing its space arrangement the way it always did:
/// seed the volume row, get that number back.
/// </summary>
internal sealed class LastKnownFreeSpaceProbe(FileTracertDbContext db) : IVolumeProbe
{
    public IReadOnlyList<ProbedVolume> EnumerateVolumes() => [];

    public ProbedVolume? TryGetByGuid(string volumeGuid) => null;

    public long? TryGetFreeBytes(string volumeGuid) =>
        db.Volumes.AsNoTracking()
            .Where(v => v.VolumeGuid == volumeGuid)
            .Select(v => (long?)v.FreeBytesLastKnown)
            .FirstOrDefault();
}

/// <summary>
/// Platform fake whose answer is set by the test, independently of what the catalog believes —
/// the disk as another process left it. <c>null</c> models a volume that does not answer at all.
/// A per-volume figure can be registered when the test needs two drives with different room;
/// anything not registered gets <see cref="FreeBytes"/>.
/// </summary>
internal sealed class StubFreeSpaceProbe(long? freeBytes) : IVolumeProbe
{
    private readonly Dictionary<string, long> _byVolume = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What an unregistered volume reports. Settable so a test can "free space" mid-scenario.</summary>
    public long? FreeBytes { get; set; } = freeBytes;

    /// <summary>How many times the device was actually asked.</summary>
    public int Probes { get; private set; }

    /// <summary>
    /// Runs after each answer is captured, so a test can model a drive that changes under the
    /// app — bytes landing during a copy, for instance.
    /// </summary>
    public Action? AfterProbe { get; set; }

    public StubFreeSpaceProbe SetVolume(string volumeGuid, long free)
    {
        _byVolume[volumeGuid] = free;
        return this;
    }

    /// <summary>Moves a registered volume's free space, the way real bytes landing or leaving would.</summary>
    public void Adjust(string volumeGuid, long deltaBytes)
    {
        if (_byVolume.TryGetValue(volumeGuid, out var current))
            _byVolume[volumeGuid] = Math.Max(0, current + deltaBytes);
    }

    public IReadOnlyList<ProbedVolume> EnumerateVolumes() => [];

    public ProbedVolume? TryGetByGuid(string volumeGuid) => null;

    public long? TryGetFreeBytes(string volumeGuid)
    {
        Probes++;
        var answer = _byVolume.TryGetValue(volumeGuid, out var free) ? free : FreeBytes;
        AfterProbe?.Invoke();
        return answer;
    }
}
