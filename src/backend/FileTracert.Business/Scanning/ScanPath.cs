namespace FileTracert.Business.Scanning;

/// <summary>
/// Pure helpers for volume-relative paths (always normalized to backslash, no
/// leading/trailing separator; the empty string is the volume root).
/// </summary>
internal static class ScanPath
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
}
