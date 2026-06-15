using FileTracert.Contracts.Platform;
using FileTracert.Data;
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
    private readonly ILogger<VolumeSyncService> _logger;

    public VolumeSyncService(IVolumeProbe probe, FileTracertDbContext db, ILogger<VolumeSyncService> logger)
    {
        _probe = probe;
        _db = db;
        _logger = logger;
    }

    public async Task SyncAsync(CancellationToken ct)
    {
        var probed = _probe.EnumerateVolumes();
        var existing = await _db.Volumes.ToListAsync(ct);
        var byGuid = existing.ToDictionary(v => v.VolumeGuid, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                VolumeMapper.ApplyLiveState(volume, p, now);
            }
            else
            {
                _db.Volumes.Add(VolumeMapper.MapNew(p, now));
            }
        }

        // Known but not currently present → offline.
        // Also reclassify Unknown volumes from persisted data (catches cloud drives that were
        // classified before the kernel-device probe was available).
        foreach (var volume in existing)
        {
            if (!seen.Contains(volume.VolumeGuid))
            {
                volume.IsOnline = false;
                VolumeMapper.ApplyOfflineReclassification(volume);
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}
