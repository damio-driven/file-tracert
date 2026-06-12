namespace FileTracert.Business.Filtering;

/// <summary>
/// The resolved file-type filter applied during a scan: global defaults from
/// <c>AppSettings</c>, optionally overridden per <c>WatchedRoot</c>.
/// </summary>
/// <param name="AllowedExtensions">
/// Lower-cased extensions (no dot) to include. Empty = allow every extension.
/// </param>
/// <param name="ExcludedPathSegments">
/// Path segments (e.g. <c>Windows</c>, <c>$Recycle.Bin</c>, <c>AppData</c>) that
/// exclude any entry whose relative path contains them.
/// </param>
/// <param name="ExcludeSystem">Exclude entries with the System attribute.</param>
/// <param name="ExcludeHidden">Exclude entries with the Hidden attribute.</param>
public sealed record EffectiveFilter(
    IReadOnlySet<string> AllowedExtensions,
    IReadOnlyList<string> ExcludedPathSegments,
    bool ExcludeSystem = true,
    bool ExcludeHidden = true);
