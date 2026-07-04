using FileTracert.Business.Setup;

namespace FileTracert.Business.Operations;

/// <summary>
/// Pure validation for the free-text names/paths that arrive from the UI with an
/// enqueue request (a folder name to create, a rename target, a move destination).
/// The queue engine already resolves and re-checks containment at execution time
/// (fix #7, <c>Platform.PathBoundary</c>), but that lives in the platform layer which
/// <c>Business</c> must not reference; this is the layer-correct, provider-agnostic
/// front gate that rejects a hostile name before it ever becomes a job.
///
/// Two shapes:
///   • <see cref="TryValidateLeaf"/> — a single path segment (rename / new-folder name):
///     no separators at all, no <c>.</c>/<c>..</c>, no drive/rooted, no reserved chars.
///   • <see cref="TryValidatePath"/> — a volume-relative destination path (move target /
///     create-folder path): may contain separators, but no traversal / rooted / drive,
///     and every segment must itself be a valid leaf. Delegates the traversal/rooted
///     rules to <see cref="WatchedRootPath"/> so the two entry points stay consistent.
/// </summary>
public static class OperationName
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// Validates a single leaf name (folder/file name, no path). Rejects empty/whitespace,
    /// any separator, <c>.</c>/<c>..</c>, drive-qualified/rooted, and characters Windows
    /// forbids in a file name.
    /// </summary>
    public static bool TryValidateLeaf(string? name, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Il nome non può essere vuoto.";
            return false;
        }

        var trimmed = name.Trim();

        if (trimmed.IndexOfAny(Separators) >= 0)
        {
            error = "Il nome non può contenere separatori di percorso ('\\' o '/').";
            return false;
        }

        if (trimmed.Contains(':'))
        {
            error = "Il nome non può contenere ':' o una lettera di unità.";
            return false;
        }

        if (trimmed is "." or "..")
        {
            error = "Il nome non può essere '.' o '..'.";
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Il nome contiene caratteri non consentiti.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates and normalizes a volume-relative destination path that must name at least
    /// one folder (rejects the empty/root path). Reuses <see cref="WatchedRootPath.TryValidate"/>
    /// for the traversal/rooted/drive rules, then checks every segment is a valid leaf.
    /// </summary>
    public static bool TryValidatePath(string? path, bool allowRoot, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (path is null)
        {
            error = "Il percorso non può essere nullo.";
            return false;
        }

        if (!WatchedRootPath.TryValidate(path, out normalized, out error))
            return false;

        if (normalized.Length == 0)
        {
            if (allowRoot)
                return true;
            error = "È necessario specificare almeno una cartella.";
            return false;
        }

        foreach (var segment in normalized.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryValidateLeaf(segment, out error))
                return false;
        }

        return true;
    }
}
