using FileTracert.Platform;

namespace FileTracert.HardwareSmoke;

/// <summary>Outcome of a guard check: whether it is safe to proceed, and why not otherwise.</summary>
public sealed record GuardResult(bool Ok, string? Reason)
{
    public static GuardResult Allow() => new(true, null);
    public static GuardResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Guard-rails the hardware-smoke harness MUST pass before doing anything destructive. Pure and
/// side-effect-free so it is fully unit-testable. Refuses to run unless explicitly enabled, and
/// rejects any configuration that could touch production data or the OS:
///   1. disabled or unset → deny;
///   2. Source / Target / Scratch that coincide with (or contain / are contained by) a production
///      WatchedRoot → deny (never operate on catalogued data);
///   3. any path that is a drive root or a system location (Windows, Program Files, …) → deny;
///   4. the three areas must be pairwise disjoint so the harness duplicates into Scratch and
///      operates on the copies, never on the Source originals.
/// </summary>
public static class HardwareSmokeGuard
{
    public static GuardResult Validate(
        HardwareSmokeOptions options,
        IReadOnlyList<string> productionWatchedRootPaths)
    {
        if (!options.Enabled)
            return GuardResult.Deny("HardwareSmoke is disabled (Enabled=false).");

        if (string.IsNullOrWhiteSpace(options.SourcePath) ||
            string.IsNullOrWhiteSpace(options.TargetPath) ||
            string.IsNullOrWhiteSpace(options.ScratchPath))
            return GuardResult.Deny("SourcePath, TargetPath and ScratchPath must all be set.");

        string source = Full(options.SourcePath);
        string target = Full(options.TargetPath);
        string scratch = Full(options.ScratchPath);

        foreach (var (label, path) in new[] { ("SourcePath", source), ("TargetPath", target), ("ScratchPath", scratch) })
        {
            if (IsDriveRoot(path))
                return GuardResult.Deny($"{label} '{path}' is a drive root — refusing to operate on a whole volume.");

            if (IsSystemLocation(path))
                return GuardResult.Deny($"{label} '{path}' is a system location — refusing to touch the OS.");
        }

        // Never touch catalogued (production) data. Compare each configured area against every
        // known WatchedRoot in both directions.
        foreach (var root in productionWatchedRootPaths)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var rootFull = Full(root);
            foreach (var (label, path) in new[] { ("SourcePath", source), ("TargetPath", target), ("ScratchPath", scratch) })
            {
                if (PathBoundary.Overlaps(path, rootFull))
                    return GuardResult.Deny(
                        $"{label} '{path}' overlaps a production WatchedRoot '{rootFull}' — refusing.");
            }
        }

        // The three areas must be disjoint: duplicating Source into Scratch and moving to Target
        // only protects the originals if Scratch is not inside Source (and Target not inside Source).
        if (PathBoundary.Overlaps(source, scratch))
            return GuardResult.Deny("ScratchPath overlaps SourcePath — the harness must operate on copies, not the originals.");
        if (PathBoundary.Overlaps(source, target))
            return GuardResult.Deny("TargetPath overlaps SourcePath — move destination must be outside the source.");
        if (PathBoundary.Overlaps(target, scratch))
            return GuardResult.Deny("TargetPath overlaps ScratchPath — keep the work area and the move target distinct.");

        return GuardResult.Allow();
    }

    private static string Full(string path) => Path.GetFullPath(path.Trim());

    private static bool IsDriveRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        return root is not null &&
               string.Equals(
                   root.TrimEnd(Path.DirectorySeparatorChar),
                   fullPath.TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemLocation(string fullPath)
    {
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        })
        {
            var dir = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(dir) && PathBoundary.IsWithin(dir, fullPath))
                return true;
        }
        return false;
    }
}
