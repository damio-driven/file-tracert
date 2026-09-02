namespace FileTracert.Contracts.Scanning;

/// <summary>
/// Pure helpers for volume-relative paths (always normalized to backslash, no
/// leading/trailing separator; the empty string is the volume root).
///
/// <para>Lives in the shared kernel (§3) because the rule it owns is not a scan detail: the same
/// spelling of "normalize", "join" and "does this path sit inside that one" has to hold in
/// <c>Business</c> (scan, enqueue guard, projection), in <c>Host</c> (the search result paths) and
/// in <c>Platform</c> (the folder browser). Every layer that reimplemented one of them by hand
/// drifted — K5 and K6 are the two times it already happened. <c>Contracts</c> depends on nothing,
/// which is exactly what a string helper everybody needs can afford.</para>
/// </summary>
public static class ScanPath
{
    /// <summary>
    /// The separator every volume-relative path in this codebase is normalized to. Here rather than
    /// re-spelled by each caller that has to BUILD with it (a subtree prefix, a join, the framed
    /// LIKE pattern of <c>FilterReconciler</c>) — the members below that only READ a path keep the
    /// char literal, because a span walk cannot take a string.
    /// </summary>
    public const string Separator = "\\";

    public static string Normalize(string path) => path.Replace('/', '\\').Trim('\\');

    public static string Parent(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? string.Empty : path[..i];
    }

    public static string Name(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>Joins a directory and a leaf name; the empty directory (volume root) yields just the name.</summary>
    public static string Join(string dir, string name) =>
        dir.Length == 0 ? name : dir + Separator + name;

    /// <summary>
    /// True when <paramref name="path"/> sits within <paramref name="root"/> (root "" = whole volume).
    ///
    /// Spelled over spans rather than as <c>Equals(path, root) || path.StartsWith(root + '\\')</c>
    /// because the scan asks this question once per enumerated item per watched root — millions of
    /// times on a real volume — and that second clause allocated a fresh <c>root + '\\'</c> string
    /// every single time (E7). Same rule, same three cases: the whole volume, an exact match, or a
    /// prefix that ends on a segment boundary so <c>Docs</c> never contains <c>Documents</c>.
    /// </summary>
    public static bool IsWithin(string path, string root) =>
        root.Length == 0 ||
        (path.Length >= root.Length &&
         (path.Length == root.Length || path[root.Length] == '\\') &&
         path.AsSpan(0, root.Length).Equals(root, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the two paths designate overlapping trees: equal, or one an ancestor of the
    /// other. Case-insensitive and segment-boundary aware, so <c>Docs</c> never overlaps
    /// <c>Documents</c>. THE single subtree-overlap predicate (K5) — every caller that has to
    /// answer "do these two operations touch the same place?" goes through here.
    /// </summary>
    public static bool Overlaps(string a, string b) => IsWithin(a, b) || IsWithin(b, a);

    /// <summary>Case-insensitive equality of two volume-relative paths.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The prefix that every strict descendant of <paramref name="path"/> starts with. Kept here
    /// so the subtree queries that must run in SQL all build it the same way.
    /// </summary>
    public static string SubtreePrefix(string path) => path + Separator;
}
