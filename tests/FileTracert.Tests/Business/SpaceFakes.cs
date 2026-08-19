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
/// </summary>
internal sealed class StubFreeSpaceProbe(long? freeBytes) : IVolumeProbe
{
    /// <summary>What the next probe reports. Settable so a test can "free space" mid-scenario.</summary>
    public long? FreeBytes { get; set; } = freeBytes;

    /// <summary>How many times the device was actually asked.</summary>
    public int Probes { get; private set; }

    public IReadOnlyList<ProbedVolume> EnumerateVolumes() => [];

    public ProbedVolume? TryGetByGuid(string volumeGuid) => null;

    public long? TryGetFreeBytes(string volumeGuid)
    {
        Probes++;
        return FreeBytes;
    }
}
