using System.Text.Json;
using FileTracert.Business.Scanning;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Filtering;

/// <summary>
/// A watched-root set already ordered from most to least specific, so answering "which root
/// governs this path?" is a first-match walk instead of a filter-and-sort.
///
/// <para>E7 — the scan asks that question for EVERY enumerated item (millions on a real volume),
/// and the ordering is a property of the root SET: it cannot change from one item to the next.
/// Rebuilding it per item — a <c>Where</c>, an <c>OrderByDescending</c> and the buffer + index map
/// its sort allocates — was the same work done over and over. The ordering lives in this type
/// rather than in a bare <c>string[]</c> so an unordered array cannot be passed by mistake: the
/// answer would be silently wrong, not a compile error.</para>
/// </summary>
public readonly struct RootsBySpecificity
{
    private readonly string[] _roots;

    private RootsBySpecificity(string[] roots) => _roots = roots;

    /// <summary>
    /// Orders the roots once. <c>OrderByDescending</c> is stable, so roots of equal length keep
    /// the caller's order — the same tie-break the per-item chain had.
    /// </summary>
    public static RootsBySpecificity Of(IEnumerable<string> normalizedRoots) =>
        new([.. normalizedRoots.OrderByDescending(root => root.Length)]);

    /// <summary>
    /// The most specific root containing <paramref name="relativePath"/>, or null when none does.
    /// First match wins, which is the same answer as "keep the containing ones, take the longest",
    /// because the candidates are visited longest first. Allocates nothing: an indexed walk over
    /// the array, and <see cref="ScanPath.IsWithin"/> no longer builds a prefix string per call.
    /// </summary>
    public string? Governing(string relativePath)
    {
        var roots = _roots;
        for (int i = 0; i < roots.Length; i++)
        {
            if (ScanPath.IsWithin(relativePath, roots[i]))
                return roots[i];
        }
        return null;
    }
}

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
    ///
    /// <para>For a one-off question. A caller that asks it in a loop orders the roots once with
    /// <see cref="RootsBySpecificity.Of"/> and reuses the result.</para>
    /// </summary>
    public static string? MostSpecificRoot(IEnumerable<string> normalizedRoots, string relativePath) =>
        RootsBySpecificity.Of(normalizedRoots).Governing(relativePath);

    /// <summary>
    /// The effective filter governing <paramref name="relativePath"/> on
    /// <paramref name="volumeId"/>. A path under no active root falls back to the global
    /// defaults: that is the widest sensible answer, and the alternative — pretending nothing
    /// is allowed — would silently exclude rows the user never asked to exclude.
    ///
    /// A malformed per-root override is not swallowed (§9): it is logged in full, naming the root
    /// and the volume, and the root falls back to the defaults — the same recovery the scan makes.
    /// It deliberately does NOT also raise a Notification the way
    /// <c>ScanService.ResolveRootFiltersAsync</c> does: the scan is the authority on filters and
    /// has already told the user about that same malformed JSON, while this runs once per renamed
    /// file, so a second channel here would repeat a message the user cannot act on any
    /// differently — once per rename.
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
