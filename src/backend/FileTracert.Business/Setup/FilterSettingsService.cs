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
/// <para>Reaching all of them brings two consequences with it, both handled below: a root whose
/// override does not parse — which now falls back to the DEFAULT filter and can therefore exclude in
/// bulk — raises a Notification rather than only a log line, and the pass is ordered so that its
/// outcome cannot depend on the order the rows come back in, which matters only for a database no
/// longer producible through this application (see the comment on the query).</para>
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
        // the outcome. An inactive root stamps ExcludedByRoot over its whole subtree, so with a
        // single pass in Id order an inactive root sitting later would overwrite the IsIncluded = 1
        // an active one that OVERLAPS it had just written, and its files would vanish from the
        // Catalog.
        //
        // Two roots on one volume cannot overlap in a database this application produced:
        // WatchedRootsService.CreateAsync rejects any path equal to, inside, or containing an
        // existing one (WatchedRootPath.Conflicts, active or not) and UpdateAsync cannot change a
        // RelativePath. So this ordering is defence against a database edited from outside — which
        // is worth two words in a query, because the failure it prevents is files silently gone from
        // the Catalog, and because reconciling ALL roots (which is what makes the global excluded
        // segments reach the overridden ones) is what would put such a database on this path at all.
        // The order-independent answer for real overlaps would be to decide every row against the
        // union of the ACTIVE roots once; nothing today can produce the input that needs it.
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
        //
        // …and therefore on CancellationToken.None, which is the house rule for everything that
        // follows a commit (11d, repeated by 11e). Here it is not a formality: the rows are already
        // excluded, the list of offending roots lives only in this local variable, there is no retry
        // and nothing durable to come back for. An abort landing in this window — the user closes
        // the tab while a bulk reconciliation finishes — would drop the only half of §9 that reaches
        // the screen, for good, while the exclusion it warns about stands.
        await ReportMalformedOverridesAsync(malformed, CancellationToken.None);

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
