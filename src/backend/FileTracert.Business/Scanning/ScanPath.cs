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

    /// <summary>True when <paramref name="path"/> sits within <paramref name="root"/> (root "" = whole volume).</summary>
    public static bool IsWithin(string path, string root) =>
        root.Length == 0 ||
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase);
}
