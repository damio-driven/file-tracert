using System.Text.Json;
using FileTracert.Business.Filtering;
using FileTracert.Contracts.Dtos;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Setup;

/// <summary>
/// Reads and updates the global default filter (<see cref="AppSettings"/>): the default file-type
/// allow-list and the excluded path segments. Changing either reconciles the existing index,
/// without a rescan.
///
/// <para><b>Every root, not only the ones on the default filter.</b> A per-root override substitutes
/// the TYPE half and nothing else — <c>ExcludedPaths</c> is global and
/// <see cref="EffectiveFilterBuilder.Build"/> applies it to every root — so skipping overridden
/// roots used to leave step 16's whole point unapplied on them, while still reporting
/// <c>NeedsScan = false</c> and counts that did not include them: a screen promising a decision it
/// had not carried out. Each root is reconciled against its OWN effective filter, which makes the
/// type half a no-op for an overridden root, and that is the correct no-op.</para>
/// </summary>
public sealed class FilterSettingsService
{
    private readonly FileTracertDbContext _db;
    private readonly FilterReconciler _reconciler;
    private readonly ILogger<FilterSettingsService> _logger;

    public FilterSettingsService(
        FileTracertDbContext db, FilterReconciler reconciler, ILogger<FilterSettingsService> logger)
    {
        _db = db;
        _reconciler = reconciler;
        _logger = logger;
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

        var roots = await _db.WatchedRoots.ToListAsync(ct);

        var included = 0;
        var excluded = 0;
        foreach (var root in roots)
        {
            var (inc, exc) = await _reconciler.ReconcileRootAsync(root, EffectiveFor(settings, root), ct);
            included += inc;
            excluded += exc;
        }

        await tx.CommitAsync(ct);
        return new ReconcileResultDto(included, excluded, FilterReconciler.FilterWidened(oldFilter, newFilter));
    }

    /// <summary>
    /// The root's own effective filter. A malformed override is logged in full and the root falls
    /// back to the defaults — the same reaction <see cref="RootFilters"/> and
    /// <see cref="RootFilterResolver"/> already have, and for the same reason: one unparseable JSON
    /// blob on one root must not stop the user from saving their settings, and it must not stop the
    /// PATH half — which that override does not govern at all — from reaching the other roots.
    /// Never swallowed (§9): the exception goes to the log whole, and the user already gets a
    /// Notification for this condition from the scan, which is the component that owns filters.
    /// </summary>
    private EffectiveFilter EffectiveFor(AppSettings settings, WatchedRoot root)
    {
        try
        {
            return EffectiveFilterBuilder.Build(settings, root.FilterOverrideJson);
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "Watched root {RootId} ('{Path}') has a malformed filter override; reconciling it " +
                "against the default filter instead.",
                root.Id, root.RelativePath);
            return EffectiveFilterBuilder.Build(settings, filterOverrideJson: null);
        }
    }
}
