using FileTracert.Contracts.Platform;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;

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

    public VolumeSyncService(IVolumeProbe probe, FileTracertDbContext db)
    {
        _probe = probe;
        _db = db;
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
            if (byGuid.TryGetValue(p.VolumeGuid, out var volume))
            {
                VolumeMapper.ApplyLiveState(volume, p, now);
            }
            else
            {
                _db.Volumes.Add(VolumeMapper.MapNew(p, now));
            }
        }

        // Known but not currently present → offline. Keep all data.
        foreach (var volume in existing)
        {
            if (!seen.Contains(volume.VolumeGuid))
            {
                volume.IsOnline = false;
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}
