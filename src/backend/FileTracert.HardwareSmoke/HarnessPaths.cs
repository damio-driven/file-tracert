namespace FileTracert.HardwareSmoke;

/// <summary>
/// The single place that decides where the harness is allowed to write. Every other component
/// (guard, fixtures, cleanup) derives its paths from here so "what the harness owns" has one
/// definition instead of one per call site.
/// </summary>
public static class HarnessPaths
{
    /// <summary>
    /// The scratch area the harness owns inside a configured test-volume folder:
    /// <c>{volumePath}\{scratchSubfolder}</c>. Nothing outside this is ever created or deleted.
    /// </summary>
    public static string ScratchAreaOf(string volumePath, string scratchSubfolder) =>
        Path.GetFullPath(Path.Combine(Path.GetFullPath(volumePath.Trim()), scratchSubfolder.Trim()));

    /// <summary>
    /// True when <paramref name="subfolder"/> is a single, safe folder name: exactly one segment,
    /// no separators, no drive, no traversal. A multi-segment or rooted value would let the
    /// scratch area (and therefore the recursive cleanup) escape the configured volume folder.
    /// </summary>
    public static bool IsSafeScratchSubfolder(string? subfolder)
    {
        if (string.IsNullOrWhiteSpace(subfolder)) return false;

        var trimmed = subfolder.Trim();
        if (trimmed is "." or "..") return false;
        if (trimmed.Contains(':')) return false;
        if (trimmed.IndexOfAny(['\\', '/']) >= 0) return false;
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        return true;
    }

    /// <summary>Folder-name-safe form of a scenario name, used for its fixture directory.</summary>
    public static string Slug(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
