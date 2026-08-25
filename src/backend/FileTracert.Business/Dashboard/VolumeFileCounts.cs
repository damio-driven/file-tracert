using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Dashboard;

/// <summary>
/// How many indexed files each volume holds — the number the Volumes list puts on every row, in
/// ONE grouped pass rather than one aggregate per volume.
///
/// <para>Lives here, next to <see cref="CatalogTotals"/> and <see cref="VolumeTotals"/>, for the
/// same reason they do: the aggregation is the thing worth measuring, and a query written inline in
/// a controller can only be measured by a test that pastes its own copy of it — which proves the
/// copy is planned well, not the product (the argument of 14a).</para>
/// </summary>
public static class VolumeFileCounts
{
    /// <param name="files">
    /// Already narrowed to what counts — catalogued and still on disk. Passing the filter in keeps
    /// the definition of "counts toward the row" at the call site, exactly as
    /// <see cref="CatalogTotals"/> does.
    /// </param>
    public static async Task<IReadOnlyDictionary<int, int>> ComputeAsync(
        IQueryable<FileEntry> files, CancellationToken ct)
        => await files
            .GroupBy(f => f.VolumeId)
            .Select(g => new { VolumeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VolumeId, x => x.Count, ct);
}
