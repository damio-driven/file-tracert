using FileTracert.Contracts.Enums;

namespace FileTracert.Business.Filtering;

/// <summary>
/// Renders an <see cref="EffectiveFilter"/> as the short Italian summary the
/// Volumi panel shows (e.g. <c>"Immagini, Video · esclude System, Hidden, $Recycle.Bin"</c>).
/// Pure: takes the resolved filter plus an extension→category lookup, returns text.
/// </summary>
public static class EffectiveFilterDescriber
{
    private static readonly IReadOnlyDictionary<FileCategory, string> CategoryLabels =
        new Dictionary<FileCategory, string>
        {
            [FileCategory.Image] = "Immagini",
            [FileCategory.Video] = "Video",
            [FileCategory.Audio] = "Audio",
            [FileCategory.Document] = "Documenti",
            [FileCategory.Archive] = "Archivi",
            [FileCategory.Other] = "Altri",
        };

    public static string Describe(EffectiveFilter filter, IReadOnlyDictionary<string, FileCategory> extensionToCategory)
    {
        var types = DescribeTypes(filter, extensionToCategory);
        var exclusions = DescribeExclusions(filter);

        return exclusions.Length == 0 ? types : $"{types} · esclude {exclusions}";
    }

    private static string DescribeTypes(EffectiveFilter filter, IReadOnlyDictionary<string, FileCategory> extensionToCategory)
    {
        if (filter.AllowedExtensions.Count == 0)
        {
            return "Tutti i tipi";
        }

        // Collapse the allowed extensions to their categories, in enum order, for a tidy label.
        var categories = filter.AllowedExtensions
            .Select(ext => extensionToCategory.TryGetValue(ext, out var cat) ? cat : FileCategory.Other)
            .Distinct()
            .OrderBy(cat => (int)cat)
            .Select(cat => CategoryLabels[cat]);

        return string.Join(", ", categories);
    }

    private static string DescribeExclusions(EffectiveFilter filter)
    {
        var parts = new List<string>();
        if (filter.ExcludeSystem)
        {
            parts.Add("System");
        }

        if (filter.ExcludeHidden)
        {
            parts.Add("Hidden");
        }

        parts.AddRange(filter.ExcludedPathSegments);
        return string.Join(", ", parts);
    }
}
