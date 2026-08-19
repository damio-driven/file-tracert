using FileTracert.Contracts.Scanning;

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

    /// <summary>
    /// The normalized form is not a second rule: it is <see cref="ScanPath.Normalize"/>, the one
    /// the scan, the enqueue and the projection already agree on. The two spellings were
    /// byte-identical (K6) and only stayed that way by luck.
    /// </summary>
    public static string Normalize(string path) => ScanPath.Normalize(path);

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
    ///
    /// <para>That question is <see cref="ScanPath.Overlaps"/> — the single subtree-overlap
    /// predicate the enqueue guard already asks (K5/K6). The local copy spelled the same three
    /// cases by hand; keeping it meant a fix to one of them would have missed the other.</para>
    /// </summary>
    public static bool Conflicts(string existing, string candidate) =>
        ScanPath.Overlaps(Normalize(existing), Normalize(candidate));
}
