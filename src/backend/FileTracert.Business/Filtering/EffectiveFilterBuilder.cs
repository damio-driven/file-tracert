using System.Text.Json;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Filtering;

/// <summary>Shape of <c>WatchedRoot.FilterOverrideJson</c>.</summary>
public sealed record FilterOverride
{
    public List<string>? Extensions { get; init; }
}

/// <summary>
/// Builds the <see cref="EffectiveFilter"/> for a watched root from the global
/// <see cref="AppSettings"/> defaults, applying a per-root JSON override when present.
/// </summary>
public static class EffectiveFilterBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Resolves the effective filter. Throws <see cref="JsonException"/> when the
    /// per-root override JSON is malformed — the caller decides how to recover
    /// (log + notify + fall back to defaults); it is never swallowed silently here.
    /// </summary>
    public static EffectiveFilter Build(AppSettings settings, string? filterOverrideJson)
    {
        var extensions = ParseOverride(filterOverrideJson)?.Extensions ?? settings.DefaultExtensionFilter;

        var allowed = extensions
            .Select(NormalizeExtension)
            .Where(e => e.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        return new EffectiveFilter(allowed, settings.ExcludedPaths.ToList());
    }

    private static FilterOverride? ParseOverride(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        // A malformed override propagates as JsonException — handled by the caller.
        return JsonSerializer.Deserialize<FilterOverride>(json, JsonOptions);
    }

    private static string NormalizeExtension(string extension) =>
        extension.TrimStart('.').ToLowerInvariant();

    /// <summary>Normalizes raw extensions: trim leading dot, lower-case, drop empties, de-duplicate.</summary>
    public static List<string> NormalizeExtensions(IEnumerable<string> extensions) =>
        extensions
            .Select(NormalizeExtension)
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();
}
