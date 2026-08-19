using System.Text.Json;
using FileTracert.Business.Scanning;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Filtering;

/// <summary>
/// Answers "which filter governs THIS path on THIS volume?" outside the scan pipeline.
///
/// The scan resolves one <see cref="EffectiveFilter"/> per watched root up front and matches
/// items to it while it walks; a single-file decision (a rename re-checking its inclusion, C19)
/// needs the same answer for one path. The rule that picks the governing root — the most
/// specific ACTIVE root that contains the path — lives here once, in
/// <see cref="MostSpecificRoot"/>, and both callers use it: two spellings of "most specific"
/// is how a file ends up included by the scan and excluded by the rename, or the reverse.
/// </summary>
public sealed class RootFilterResolver
{
    private readonly FileTracertDbContext _db;
    private readonly ILogger<RootFilterResolver> _logger;

    public RootFilterResolver(FileTracertDbContext db, ILogger<RootFilterResolver> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// The most specific root of <paramref name="normalizedRoots"/> that contains
    /// <paramref name="relativePath"/>, or null when none does. Roots must already be
    /// normalized (<see cref="ScanPath.Normalize"/>); containment is the single
    /// segment-aware, case-insensitive predicate <see cref="ScanPath.IsWithin"/>.
    /// </summary>
    public static string? MostSpecificRoot(IEnumerable<string> normalizedRoots, string relativePath) =>
        normalizedRoots
            .Where(root => ScanPath.IsWithin(relativePath, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();

    /// <summary>
    /// The effective filter governing <paramref name="relativePath"/> on
    /// <paramref name="volumeId"/>. A path under no active root falls back to the global
    /// defaults: that is the widest sensible answer, and the alternative — pretending nothing
    /// is allowed — would silently exclude rows the user never asked to exclude.
    ///
    /// A malformed per-root override is not swallowed (§9): it is logged in full and the root
    /// falls back to the defaults, exactly as the scan does with the same input.
    /// </summary>
    public async Task<EffectiveFilter> ResolveForPathAsync(
        int volumeId, string relativePath, CancellationToken ct)
    {
        var settings = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppSettings();

        var roots = await _db.WatchedRoots.AsNoTracking()
            .Where(r => r.VolumeId == volumeId && r.IsActive)
            .Select(r => new { r.RelativePath, r.FilterOverrideJson })
            .ToListAsync(ct);

        var byKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            byKey[ScanPath.Normalize(root.RelativePath)] = root.FilterOverrideJson;
        }

        var key = MostSpecificRoot(byKey.Keys, ScanPath.Normalize(relativePath));
        var overrideJson = key is not null ? byKey[key] : null;

        try
        {
            return EffectiveFilterBuilder.Build(settings, overrideJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Invalid filter override for root '{Root}' on volume {VolumeId}; using the default filter.",
                key, volumeId);
            return EffectiveFilterBuilder.Build(settings, filterOverrideJson: null);
        }
    }
}
