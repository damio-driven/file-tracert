using System.Text.Json;
using FileTracert.Contracts.Scanning;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Filtering;

/// <summary>
/// One effective filter per active watched root, keyed by the root's normalized relative path —
/// the shape both indexing paths need before they can ask "does this item belong in the index?".
///
/// <para>The full scan and the incremental USN delta must answer that question identically, so the
/// rule that turns (settings + per-root override) into a filter map lives here once. They differ
/// only in what they DO about a malformed override, which is why the reaction is a callback
/// instead of a branch: the scan is the authority on filters and raises a user-visible
/// notification, while the delta runs every few seconds and would turn one bad override into a
/// stream of identical messages the user cannot act on any differently (the same reasoning
/// <see cref="RootFilterResolver"/> already applies to the single-path case).</para>
/// </summary>
public static class RootFilters
{
    /// <param name="settings">The singleton settings row, or null before it is seeded.</param>
    /// <param name="roots">The ACTIVE watched roots of one volume.</param>
    /// <param name="onInvalidOverride">
    /// Called with the offending root and the parse failure. Never swallowed (§9): whatever the
    /// caller does with it, the root then falls back to the default filter so indexing proceeds.
    /// </param>
    public static async Task<Dictionary<string, EffectiveFilter>> ResolveAsync(
        AppSettings? settings,
        IEnumerable<WatchedRoot> roots,
        Func<WatchedRoot, JsonException, Task> onInvalidOverride,
        CancellationToken ct)
    {
        var effectiveSettings = settings ?? new AppSettings();
        var filters = new Dictionary<string, EffectiveFilter>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var key = ScanPath.Normalize(root.RelativePath);
            try
            {
                filters[key] = EffectiveFilterBuilder.Build(effectiveSettings, root.FilterOverrideJson);
            }
            catch (JsonException ex)
            {
                await onInvalidOverride(root, ex);
                filters[key] = EffectiveFilterBuilder.Build(effectiveSettings, filterOverrideJson: null);
            }
        }

        return filters;
    }
}
