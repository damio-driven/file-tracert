using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;

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
    /// The PERIMETER half of the filter, and WHICH of its two rules rejected the item: null when
    /// it is inside, otherwise <see cref="ScanSkipCause.ExcludedPath"/> or
    /// <see cref="ScanSkipCause.ExcludedAttributes"/>. Never
    /// <see cref="ScanSkipCause.InactiveRoot"/> — the roots are asked before the filter is, and an
    /// item outside every active root is never offered to it.
    ///
    /// <para>Step 16 made the answer a cause rather than a bool because the two rules are undone by
    /// different owners, exactly as step 11h found for the three causes it split: a path segment is
    /// a fact of the settings and reconciliation retracts it with no disk read, while an attribute
    /// is a fact of the disk and only another scan can. Folded into one verdict, the first was
    /// hostage to the second.</para>
    ///
    /// <para>The PATH is asked first, and that precedence is deliberate. Attributing to the
    /// attribute cause something that is also path-excluded would pin it there for the lifetime of
    /// the row — the reconciler would refuse to touch it however the segments change. Erring the
    /// other way costs at most one scan, which is the same trade the pessimistic backfill makes,
    /// read from the other end.</para>
    ///
    /// <para>One <see cref="IsPathExcluded"/> call, not two: it splits the path on every
    /// invocation, and the 11g review had already taken a second split off this branch.</para>
    /// </summary>
    public static ScanSkipCause? PerimeterCause(
        string relativePath, FileAttributes attributes, EffectiveFilter filter)
    {
        if (IsPathExcluded(relativePath, filter))
        {
            return ScanSkipCause.ExcludedPath;
        }

        return IsExcludedByAttributes(attributes, filter) ? ScanSkipCause.ExcludedAttributes : null;
    }

    /// <summary>
    /// The PERIMETER half of the filter as a yes/no, for the callers that do not need to record
    /// which rule spoke. <see cref="PerimeterCause"/> is the same question with its answer kept.
    /// </summary>
    public static bool IsInsidePerimeter(string relativePath, FileAttributes attributes, EffectiveFilter filter) =>
        PerimeterCause(relativePath, attributes, filter) is null;

    /// <summary>
    /// Directories are not filtered by extension (the tree needs them), so the perimeter rules
    /// are all there is to ask.
    /// </summary>
    public static bool ShouldIncludeDirectory(string relativePath, FileAttributes attributes, EffectiveFilter filter) =>
        IsInsidePerimeter(relativePath, attributes, filter);

    /// <summary>
    /// The TYPE half: the extension allow-list, empty meaning "every type". The other half of
    /// <see cref="ShouldIncludeFile"/>, spelled apart because a caller sometimes needs to know
    /// WHICH half rejected a file — the scan does, to tell "outside the perimeter" (recorded on
    /// the row) from "wrong type" (never indexed in the first place).
    /// </summary>
    public static bool IsAllowedType(string extension, EffectiveFilter filter) =>
        filter.AllowedExtensions.Count == 0 || filter.AllowedExtensions.Contains(extension);

    /// <summary>Files honor the perimeter rules plus the extension allow-list.</summary>
    public static bool ShouldIncludeFile(
        string relativePath,
        string extension,
        FileAttributes attributes,
        EffectiveFilter filter) =>
        IsInsidePerimeter(relativePath, attributes, filter) && IsAllowedType(extension, filter);

    private static bool IsExcludedByAttributes(FileAttributes attributes, EffectiveFilter filter) =>
        (filter.ExcludeSystem && attributes.HasFlag(FileAttributes.System)) ||
        (filter.ExcludeHidden && attributes.HasFlag(FileAttributes.Hidden));
}
