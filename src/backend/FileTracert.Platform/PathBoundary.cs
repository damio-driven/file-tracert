namespace FileTracert.Platform;

/// <summary>
/// Path-containment guard extracted from the write-path check (fix #7). Answers the single
/// question "does this resolved full path stay inside that boundary directory?" with a
/// trailing-separator comparison, so a sibling (<c>C:\Mount2</c>) is never accepted for the
/// boundary <c>C:\Mount</c>. Reused by <see cref="Win32FileMover"/> and by the hardware-smoke
/// harness's guard-rails.
/// </summary>
public static class PathBoundary
{
    /// <summary>
    /// True when <paramref name="candidateFullPath"/> is <paramref name="boundaryDir"/> itself or
    /// lies underneath it. Both operands are normalized with <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    public static bool IsWithin(string boundaryDir, string candidateFullPath)
    {
        var boundaryFull = Path.GetFullPath(boundaryDir);
        var candidate = Path.GetFullPath(candidateFullPath);

        var boundary = boundaryFull.EndsWith(Path.DirectorySeparatorChar)
            ? boundaryFull
            : boundaryFull + Path.DirectorySeparatorChar;

        return candidate.Equals(boundary.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the two directories overlap in either direction (equal, or one contains the
    /// other). Used to keep the smoke harness's Source / Target / Scratch areas disjoint.
    /// </summary>
    public static bool Overlaps(string a, string b) =>
        IsWithin(a, b) || IsWithin(b, a);
}
