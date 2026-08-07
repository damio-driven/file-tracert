using FileTracert.Platform;

namespace FileTracert.HardwareSmoke;

/// <summary>Outcome of a guard check: whether it is safe to proceed, and why not otherwise.</summary>
public sealed record GuardResult(bool Ok, string? Reason)
{
    public static GuardResult Allow() => new(true, null);
    public static GuardResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Guard-rails the hardware harness MUST pass before doing anything destructive. Pure apart from
/// the directory-existence probe, so it is fully unit-testable. It refuses to run unless
/// explicitly enabled, and rejects any configuration that could touch production data or the OS:
///   1. disabled, or no usable test volume → deny;
///   2. a scratch subfolder that is not a single safe folder name → deny (the recursive cleanup
///      must never be able to escape the configured folder);
///   3. a configured path that does not exist → deny (a typo must not create a work area
///      somewhere unintended);
///   4. a path that is a drive root or a system location (Windows, Program Files, …) → deny;
///   5. a path that coincides with / contains / is contained by a production WatchedRoot → deny
///      (the harness never operates anywhere near catalogued data);
///   6. two test volumes whose paths overlap → deny (their scratch areas would collide, and a
///      cleanup of one would delete the other's fixtures).
/// The harness only ever creates and destroys content inside <c>{path}\{ScratchSubfolder}</c>:
/// pre-existing content in the configured folders is never read, moved or deleted.
/// </summary>
public static class HardwareSmokeGuard
{
    public static GuardResult Validate(
        HardwareSmokeOptions options,
        IReadOnlyList<string> productionWatchedRootPaths)
    {
        if (!options.Enabled)
            return GuardResult.Deny("HardwareSmoke is disabled (Enabled=false).");

        if (!HarnessPaths.IsSafeScratchSubfolder(options.ScratchSubfolder))
            return GuardResult.Deny(
                $"ScratchSubfolder '{options.ScratchSubfolder}' must be a single folder name " +
                "(no separators, no drive, no '..').");

        var volumes = options.TestVolumes ?? [];
        if (volumes.Count == 0)
            return GuardResult.Deny("No TestVolumes configured.");

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<(string Name, string Path)>(volumes.Count);

        foreach (var volume in volumes)
        {
            if (string.IsNullOrWhiteSpace(volume.Name))
                return GuardResult.Deny("Every TestVolume needs a Name.");

            if (!seenNames.Add(volume.Name.Trim()))
                return GuardResult.Deny($"Duplicate TestVolume name '{volume.Name}'.");

            if (string.IsNullOrWhiteSpace(volume.Path))
                return GuardResult.Deny($"TestVolume '{volume.Name}' has no Path.");

            var full = Path.GetFullPath(volume.Path.Trim());

            if (!Directory.Exists(full))
                return GuardResult.Deny(
                    $"TestVolume '{volume.Name}' path '{full}' does not exist — create it first " +
                    "so the harness never invents a work area from a typo.");

            if (IsDriveRoot(full))
                return GuardResult.Deny(
                    $"TestVolume '{volume.Name}' path '{full}' is a drive root — refusing to operate on a whole volume.");

            if (IsSystemLocation(full))
                return GuardResult.Deny(
                    $"TestVolume '{volume.Name}' path '{full}' is a system location — refusing to touch the OS.");

            foreach (var root in productionWatchedRootPaths)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var rootFull = Path.GetFullPath(root);
                if (PathBoundary.Overlaps(full, rootFull))
                    return GuardResult.Deny(
                        $"TestVolume '{volume.Name}' path '{full}' overlaps a production WatchedRoot '{rootFull}' — refusing.");
            }

            resolved.Add((volume.Name.Trim(), full));
        }

        // Overlapping areas would share (or nest) their scratch folders: one scenario's cleanup
        // would then delete another's fixtures, and an "untouched source" assert would be a lie.
        for (int i = 0; i < resolved.Count; i++)
        {
            for (int j = i + 1; j < resolved.Count; j++)
            {
                if (PathBoundary.Overlaps(resolved[i].Path, resolved[j].Path))
                    return GuardResult.Deny(
                        $"TestVolumes '{resolved[i].Name}' and '{resolved[j].Name}' overlap " +
                        $"('{resolved[i].Path}' vs '{resolved[j].Path}') — they must be disjoint.");
            }
        }

        return GuardResult.Allow();
    }

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
