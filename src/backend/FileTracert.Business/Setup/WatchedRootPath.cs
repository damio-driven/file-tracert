namespace FileTracert.Business.Setup;

/// <summary>
/// Pure validation/normalization for volume-relative watched-root paths. The path
/// arrives from the untrusted UI: it must stay inside the volume root (no
/// traversal, no absolute/drive-qualified path). Normalized form = backslash,
/// no leading/trailing separator; empty string = volume root.
/// </summary>
public static class WatchedRootPath
{
    private static readonly char[] Separators = ['\\', '/'];

    public static string Normalize(string path) => path.Replace('/', '\\').Trim('\\');

    /// <summary>
    /// Validates and normalizes a candidate path. Rejects absolute/UNC/drive-qualified
    /// paths and any <c>..</c> segment. Empty (volume root) is valid.
    /// </summary>
    public static bool TryValidate(string path, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (path.Contains(':'))
        {
            error = "Il percorso non può essere assoluto o contenere una lettera di unità.";
            return false;
        }

        // UNC paths (\\server\share) are absolute even after slash unification.
        if (path.Replace('/', '\\').StartsWith("\\\\", StringComparison.Ordinal))
        {
            error = "Il percorso deve essere relativo alla radice del volume.";
            return false;
        }

        var candidate = Normalize(path);
        // A drive-rooted path (\\?\, X:\) is absolute; a single leading slash is just
        // relative-to-volume-root and was stripped by Normalize above.
        if (Path.IsPathRooted(candidate))
        {
            error = "Il percorso deve essere relativo alla radice del volume.";
            return false;
        }

        var segments = candidate.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                error = "Il percorso non può contenere '.' o '..'.";
                return false;
            }
        }

        normalized = string.Join('\\', segments);
        return true;
    }

    /// <summary>
    /// True when two normalized roots cannot coexist: equal, or one is an ancestor
    /// of the other (segment-aware, so "Foto" does not conflict with "Fotografie").
    /// </summary>
    public static bool Conflicts(string existing, string candidate)
    {
        var a = Normalize(existing);
        var b = Normalize(candidate);

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsAncestor(a, b) || IsAncestor(b, a);
    }

    private static bool IsAncestor(string ancestor, string descendant)
    {
        if (ancestor.Length == 0)
        {
            return true;
        }

        return descendant.StartsWith(ancestor + '\\', StringComparison.OrdinalIgnoreCase);
    }
}
