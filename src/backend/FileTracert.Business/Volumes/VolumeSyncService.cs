using FileTracert.Business.Realtime;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Volumes;

/// <summary>
/// Reconciles the volumes currently present on the system (via <see cref="IVolumeProbe"/>)
/// with the persisted catalog: upsert by GUID, refresh live state, mark missing
/// volumes offline. Never deletes — a disconnected volume is just offline.
/// </summary>
public sealed class VolumeSyncService
{
    private readonly IVolumeProbe _probe;
    private readonly FileTracertDbContext _db;
    private readonly RealtimeEvents _realtime;
    private readonly ILogger<VolumeSyncService> _logger;

    public VolumeSyncService(
        IVolumeProbe probe,
        FileTracertDbContext db,
        RealtimeEvents realtime,
        ILogger<VolumeSyncService> logger)
    {
        _probe = probe;
        _db = db;
        _realtime = realtime;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles the catalog with the probe and returns the IDs of the volumes that went from
    /// offline to online in THIS cycle. That transition is the trigger the queue waits for: jobs
    /// parked because their drive was missing are re-evaluated on it (§4, finding #13). Newly
    /// discovered volumes are not reported — no job can be waiting for a volume the catalog has
    /// never seen.
    /// </summary>
    public async Task<IReadOnlyList<int>> SyncAsync(CancellationToken ct)
    {
        var probed = _probe.EnumerateVolumes();
        var existing = await _db.Volumes.ToListAsync(ct);
        var byGuid = existing.ToDictionary(v => v.VolumeGuid, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cameOnline = new List<Volume>();
        // Volumes whose pushed state actually moved. A quiet cycle — the common case, every few
        // seconds — must publish nothing at all, or the client would repaint the dashboard on a
        // timer for no reason.
        var changed = new List<Volume>();
        var newlyDiscovered = new List<Volume>();

        foreach (var p in probed)
        {
            seen.Add(p.VolumeGuid);

            // Diagnostic: log raw classifier signals so cloud-volume misclassifications are visible.
            var kind = VolumeClassifier.Classify(p);
            _logger.LogDebug(
                "Volume {Guid} ({Label}): HasPhysicalExtents={HasExtents}, PhysicalDiskId={DiskId}, " +
                "DriveType={DriveType} → Kind={Kind}, DefaultCatalogable={Catalogable}",
                p.VolumeGuid, p.Label ?? "(no label)",
                p.HasPhysicalExtents, p.PhysicalDiskId ?? "(null)",
                p.DriveType, kind, VolumeClassifier.DefaultCatalogable(kind));

            if (byGuid.TryGetValue(p.VolumeGuid, out var volume))
            {
                // Capture the transition BEFORE the mapper flips IsOnline to true.
                if (!volume.IsOnline)
                {
                    cameOnline.Add(volume);
                }

                var wasOnline = volume.IsOnline;
                var previousFreeBytes = volume.FreeBytesLastKnown;

                VolumeMapper.ApplyLiveState(volume, p, now);

                if (wasOnline != volume.IsOnline || previousFreeBytes != volume.FreeBytesLastKnown)
                {
                    changed.Add(volume);
                }
            }
            else
            {
                var added = VolumeMapper.MapNew(p, now);
                _db.Volumes.Add(added);
                // Id is assigned by the save below; announce it only after it exists.
                newlyDiscovered.Add(added);
            }
        }

        // Known but not currently present → offline.
        // Also reclassify Unknown volumes from persisted data (catches cloud drives that were
        // classified before the kernel-device probe was available).
        foreach (var volume in existing)
        {
            if (!seen.Contains(volume.VolumeGuid))
            {
                if (volume.IsOnline)
                {
                    changed.Add(volume);
                }

                volume.IsOnline = false;
                VolumeMapper.ApplyOfflineReclassification(volume);
            }
        }

        await _db.SaveChangesAsync(ct);

        if (cameOnline.Count > 0)
        {
            _logger.LogInformation(
                "Volume sync: {Count} volume(s) came back online ({Guids}).",
                cameOnline.Count, string.Join(", ", cameOnline.Select(v => v.VolumeGuid)));
        }

        // Published after the save, so every id exists and every value is the committed one.
        foreach (var volume in changed.Concat(newlyDiscovered))
        {
            await _realtime.VolumeStatusChangedAsync(
                volume.Id, volume.IsOnline, volume.FreeBytesLastKnown, volume.LastSeenUtc);
        }

        return [.. cameOnline.Select(v => v.Id)];
    }
}
