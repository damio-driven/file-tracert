using FileTracert.Business.Filtering;
using FileTracert.Contracts.Dtos;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Setup;

/// <summary>
/// Reads and updates the global default file-type filter (<see cref="AppSettings"/>).
/// Changing it reconciles the existing index for every root that uses the default
/// (no per-root override), without a rescan.
/// </summary>
public sealed class FilterSettingsService
{
    private readonly FileTracertDbContext _db;
    private readonly FilterReconciler _reconciler;

    public FilterSettingsService(FileTracertDbContext db, FilterReconciler reconciler)
    {
        _db = db;
        _reconciler = reconciler;
    }

    public async Task<FilterSettingsDto> GetAsync(CancellationToken ct)
    {
        var settings = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppSettings();
        return new FilterSettingsDto(settings.DefaultExtensionFilter, settings.ExcludedPaths);
    }

    public async Task<ReconcileResultDto> UpdateAsync(FilterSettingsDto request, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var settings = await _db.AppSettings.FirstAsync(ct);
        var oldFilter = EffectiveFilterBuilder.Build(settings, filterOverrideJson: null);

        settings.DefaultExtensionFilter = EffectiveFilterBuilder.NormalizeExtensions(request.AllowedExtensions);
        settings.ExcludedPaths = request.ExcludedPaths.ToList();
        await _db.SaveChangesAsync(ct);

        var newFilter = EffectiveFilterBuilder.Build(settings, filterOverrideJson: null);

        var roots = await _db.WatchedRoots
            .Where(r => r.FilterOverrideJson == null)
            .ToListAsync(ct);

        var included = 0;
        var excluded = 0;
        foreach (var root in roots)
        {
            var (inc, exc) = await _reconciler.ReconcileRootAsync(root, newFilter, ct);
            included += inc;
            excluded += exc;
        }

        await tx.CommitAsync(ct);
        return new ReconcileResultDto(included, excluded, FilterReconciler.FilterWidened(oldFilter, newFilter));
    }
}
