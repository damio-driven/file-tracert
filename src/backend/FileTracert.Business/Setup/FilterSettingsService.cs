using System.Text.Json;
using FileTracert.Business.Filtering;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
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
///
/// <para>Reaching all of them brings two consequences with it, both handled below: the pass is
/// ordered so that an INACTIVE root can never overwrite an overlapping active one, and a root whose
/// override does not parse — which now falls back to the DEFAULT filter and can therefore exclude in
/// bulk — raises a Notification rather than only a log line.</para>
/// </summary>
public sealed class FilterSettingsService
{
    private readonly FileTracertDbContext _db;
    private readonly FilterReconciler _reconciler;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<FilterSettingsService> _logger;

    public FilterSettingsService(
        FileTracertDbContext db,
        FilterReconciler reconciler,
        INotificationPublisher notifications,
        ILogger<FilterSettingsService> logger)
    {
        _db = db;
        _reconciler = reconciler;
        _notifications = notifications;
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

        // INACTIVE roots first, ACTIVE ones after — so the order the rows come back in cannot decide
        // the outcome. An inactive root stamps ExcludedByRoot over its whole subtree, and two roots
        // are free to overlap: with a single pass in Id order, an inactive root that happened to sit
        // later would overwrite the IsIncluded = 1 an active one had just written, and its files
        // would vanish from the Catalog. Sorting is the cheap half of the fix; the correct one is to
        // compute the union of the ACTIVE roots once and decide every row against it, which is a
        // round of its own — this makes the result order-independent in the meantime.
        var roots = await _db.WatchedRoots.OrderBy(r => r.IsActive).ThenBy(r => r.Id).ToListAsync(ct);

        var included = 0;
        var excluded = 0;
        var malformed = new List<(WatchedRoot Root, JsonException Error)>();
        foreach (var root in roots)
        {
            var (inc, exc) = await _reconciler.ReconcileRootAsync(root, EffectiveFor(settings, root, malformed), ct);
            included += inc;
            excluded += exc;
        }

        await tx.CommitAsync(ct);

        // After the commit, never inside it: the notification is a row of its own plus a realtime
        // push, and the settings save must not depend on either.
        await ReportMalformedOverridesAsync(malformed, ct);

        return new ReconcileResultDto(included, excluded, FilterReconciler.FilterWidened(oldFilter, newFilter));
    }

    /// <summary>
    /// The root's own effective filter. A malformed override is logged in full and the root falls
    /// back to the defaults — the same reaction <see cref="RootFilters"/> and
    /// <see cref="RootFilterResolver"/> already have, and for the same reason: one unparseable JSON
    /// blob on one root must not stop the user from saving their settings, and it must not stop the
    /// PATH half — which that override does not govern at all — from reaching the other roots.
    ///
    /// <para>The fallback is not harmless, which is why it is also collected for a Notification.
    /// The default allow-list can be NARROWER than the override the user wrote, so reconciling
    /// against it stamps <c>ExcludedByType = 1, IsIncluded = 0</c> on every row whose extension the
    /// override admitted and the default does not — a bulk exclusion produced by pressing Save, and
    /// reported in the counts as though it were what was asked for. A log line alone is half of §9;
    /// this is a user expectation going wrong, so it has to reach the screen.</para>
    /// </summary>
    private EffectiveFilter EffectiveFor(
        AppSettings settings, WatchedRoot root, List<(WatchedRoot Root, JsonException Error)> malformed)
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
            malformed.Add((root, ex));
            return EffectiveFilterBuilder.Build(settings, filterOverrideJson: null);
        }
    }

    /// <summary>
    /// Tells the user what was reconciled against a filter they did not choose. One notification per
    /// offending root: they are a handful by construction, and naming the root is the only way the
    /// message is actionable.
    /// </summary>
    private async Task ReportMalformedOverridesAsync(
        List<(WatchedRoot Root, JsonException Error)> malformed, CancellationToken ct)
    {
        foreach (var (root, error) in malformed)
        {
            await _notifications.PublishAsync(
                NotificationSeverity.Warning,
                "Setup",
                $"Filtro non valido per «{root.RelativePath}»",
                "L'override del filtro di questa cartella è malformato: le sue righe sono state " +
                $"riallineate al filtro predefinito, che può escluderne di più. Dettaglio: {error.Message}",
                root.VolumeId,
                ct);
        }
    }
}
