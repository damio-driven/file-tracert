namespace FileTracert.Business.Scanning;

/// <summary>
/// Pure helpers for volume-relative paths (always normalized to backslash, no
/// leading/trailing separator; the empty string is the volume root).
/// </summary>
public static class ScanPath
{
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
        dir.Length == 0 ? name : dir + "\\" + name;

    /// <summary>True when <paramref name="path"/> sits within <paramref name="root"/> (root "" = whole volume).</summary>
    public static bool IsWithin(string path, string root) =>
        root.Length == 0 ||
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase);

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
    public static string SubtreePrefix(string path) => path + "\\";
}
