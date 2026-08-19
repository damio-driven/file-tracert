using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Dashboard;

/// <summary>
/// The two index figures the Dashboard headline states — how many files are catalogued and how
/// many bytes they add up to — read in ONE pass (E6).
///
/// They used to be a <c>LongCountAsync</c> followed by a <c>SumAsync</c>: two sequential full
/// aggregates over <c>Files</c>, which is the largest table in the database and the one the scan
/// is writing to. On a catalogued volume that is millions of rows walked twice to answer one card,
/// and on SQLite — single writer — every avoidable pass is time nobody else can write in.
///
/// The <c>GroupBy(_ => 1)</c> idiom is the same one <see cref="QueueTotals"/> uses: it also
/// replaces the old <c>totalFiles == 0 ? 0 : …</c> guard, which existed because <c>SUM()</c> over
/// no rows is NULL and does not fit a non-nullable <c>long</c>. An empty table now simply yields no
/// group, and <see cref="Empty"/> answers for it.
/// </summary>
public sealed record CatalogTotals(long TotalFiles, long TotalBytes)
{
    public static readonly CatalogTotals Empty = new(0, 0);

    /// <param name="files">
    /// Already narrowed to what counts — catalogued and still on disk. Passing the filter in keeps
    /// the definition of "counts toward the totals" at the call site, where the DTO is assembled,
    /// instead of splitting it between here and there.
    /// </param>
    public static async Task<CatalogTotals> ComputeAsync(IQueryable<FileEntry> files, CancellationToken ct)
    {
        var totals = await files
            .GroupBy(_ => 1)
            .Select(g => new CatalogTotals(g.LongCount(), g.Sum(f => f.SizeBytes)))
            .FirstOrDefaultAsync(ct);

        return totals ?? Empty;
    }
}

/// <summary>
/// How many volumes the catalog knows and how many are connected right now — one aggregate rather
/// than a count and a filtered count (E6). The table is small, so this is not about the rows: it
/// is one round trip instead of two on a request the UI makes on every Dashboard load and on every
/// reconnection.
/// </summary>
public sealed record VolumeTotals(int Total, int Online)
{
    public static readonly VolumeTotals Empty = new(0, 0);

    public static async Task<VolumeTotals> ComputeAsync(IQueryable<Volume> volumes, CancellationToken ct)
    {
        var totals = await volumes
            .GroupBy(_ => 1)
            .Select(g => new VolumeTotals(g.Count(), g.Count(v => v.IsOnline)))
            .FirstOrDefaultAsync(ct);

        return totals ?? Empty;
    }
}
