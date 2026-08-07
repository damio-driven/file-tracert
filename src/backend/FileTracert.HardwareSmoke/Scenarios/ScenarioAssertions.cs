using System.Security.Cryptography;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// Thrown by a scenario that cannot produce a meaningful verdict on this machine (a copy that
/// finished before it could be interrupted, a pair without an external drive, …). Reported as
/// SKIPPED, never as a pass: an assertion that never ran must not look like one that succeeded.
/// </summary>
public sealed class ScenarioSkippedException : Exception
{
    public ScenarioSkippedException(string reason) : base(reason) { }
}

/// <summary>
/// Collects a scenario's assertion failures instead of throwing on the first one, so a single run
/// reports everything that is wrong (e.g. "file missing on target" AND "partial left behind")
/// rather than only the first symptom. Every failure carries the concrete paths/values involved.
/// </summary>
public sealed class ScenarioAssertions
{
    public const string PartialSuffix = ".fadit-partial";

    private readonly List<string> _failures = [];

    public IReadOnlyList<string> Failures => _failures;
    public bool AnyFailed => _failures.Count > 0;

    public void Fail(string what) => _failures.Add(what);

    public void True(bool condition, string what)
    {
        if (!condition) _failures.Add(what);
    }

    public void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            _failures.Add($"{what}: expected '{expected}', got '{actual}'.");
    }

    public void FileExists(string absolutePath, string why)
    {
        if (!File.Exists(absolutePath))
            _failures.Add($"{why}: file '{absolutePath}' does not exist.");
    }

    public void FileMissing(string absolutePath, string why)
    {
        if (File.Exists(absolutePath))
            _failures.Add($"{why}: file '{absolutePath}' still exists.");
    }

    public void DirectoryExists(string absolutePath, string why)
    {
        if (!Directory.Exists(absolutePath))
            _failures.Add($"{why}: directory '{absolutePath}' does not exist.");
    }

    public void DirectoryMissing(string absolutePath, string why)
    {
        if (Directory.Exists(absolutePath))
            _failures.Add($"{why}: directory '{absolutePath}' still exists.");
    }

    /// <summary>No <c>.fadit-partial</c> may survive anywhere under the given tree, in any outcome
    /// (success, failure, cancel or crash) — a leftover partial is an orphan by definition.</summary>
    public void NoPartialsUnder(string absoluteDirectory, string why)
    {
        if (!Directory.Exists(absoluteDirectory)) return;

        var partials = Directory
            .EnumerateFiles(absoluteDirectory, "*" + PartialSuffix, SearchOption.AllDirectories)
            .ToList();

        if (partials.Count > 0)
            _failures.Add($"{why}: {partials.Count} leftover partial(s): {string.Join("; ", partials)}");
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
