using FileTracert.Contracts.Enums;

namespace FileTracert.Business.Filtering;

/// <summary>
/// Pure, testable inclusion logic applied <em>inside</em> the scan pipeline so the
/// index is born lean (CLAUDE.md §4). No I/O, no DB.
/// </summary>
public static class FileFilter
{
    private static readonly char[] PathSeparators = ['\\', '/'];

    /// <summary>Extracts the lower-cased extension (no dot) from a file name; empty when none.</summary>
    public static string GetExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
        {
            return string.Empty;
        }

        return name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>Resolves a file category from its extension via the seeded map; unknown → Other.</summary>
    public static FileCategory ResolveCategory(string extension, IReadOnlyDictionary<string, FileCategory> map) =>
        map.TryGetValue(extension, out var category) ? category : FileCategory.Other;

    /// <summary>True when any segment of the relative path matches an excluded segment.</summary>
    public static bool IsPathExcluded(string relativePath, EffectiveFilter filter)
    {
        if (filter.ExcludedPathSegments.Count == 0 || relativePath.Length == 0)
        {
            return false;
        }

        var segments = relativePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            foreach (var excluded in filter.ExcludedPathSegments)
            {
                if (string.Equals(segment, excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The PERIMETER half of the filter: the rules that have nothing to do with the file's type —
    /// excluded attributes and excluded path segments. Spelled once because the two halves are
    /// undone differently (§4): re-widening the type allow-list is <c>FilterReconciler</c>'s job
    /// on the rows already indexed, while an attribute or a path segment only changes what a scan
    /// walks into, so a row that fails THIS is one the scan deliberately skipped.
    /// </summary>
    public static bool IsInsidePerimeter(string relativePath, FileAttributes attributes, EffectiveFilter filter) =>
        !IsExcludedByAttributes(attributes, filter) && !IsPathExcluded(relativePath, filter);

    /// <summary>
    /// Directories are not filtered by extension (the tree needs them), so the perimeter rules
    /// are all there is to ask.
    /// </summary>
    public static bool ShouldIncludeDirectory(string relativePath, FileAttributes attributes, EffectiveFilter filter) =>
        IsInsidePerimeter(relativePath, attributes, filter);

    /// <summary>Files honor the perimeter rules plus the extension allow-list.</summary>
    public static bool ShouldIncludeFile(
        string relativePath,
        string extension,
        FileAttributes attributes,
        EffectiveFilter filter)
    {
        if (!IsInsidePerimeter(relativePath, attributes, filter))
        {
            return false;
        }

        return filter.AllowedExtensions.Count == 0 || filter.AllowedExtensions.Contains(extension);
    }

    private static bool IsExcludedByAttributes(FileAttributes attributes, EffectiveFilter filter) =>
        (filter.ExcludeSystem && attributes.HasFlag(FileAttributes.System)) ||
        (filter.ExcludeHidden && attributes.HasFlag(FileAttributes.Hidden));
}
