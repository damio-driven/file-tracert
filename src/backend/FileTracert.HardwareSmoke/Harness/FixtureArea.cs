using FileTracert.Contracts.Scanning;

namespace FileTracert.HardwareSmoke.Harness;

/// <summary>
/// The slice of a test volume one scenario role (source or target) owns:
/// <c>{volume scratch}\{scenario}\{role}</c>. Every file a scenario operates on is created here
/// by the harness itself — it never adopts pre-existing user content, which is what makes the
/// destructive operations safe to run against a folder that also holds real data.
/// </summary>
public sealed class FixtureArea
{
    private const int ContentBufferSize = 1 << 20; // 1 MB write chunks

    private readonly List<string> _created = [];

    public FixtureArea(TestVolume volume, string scenarioSlug, string role)
    {
        Volume = volume;
        RootFullPath = Path.Combine(volume.ScratchFullPath, scenarioSlug, role);
        RootRelativePath = ScanPath.Join(
            ScanPath.Join(volume.ScratchRelativePath, scenarioSlug), role);
        Directory.CreateDirectory(RootFullPath);
    }

    public TestVolume Volume { get; }

    /// <summary>Absolute path of the area root.</summary>
    public string RootFullPath { get; }

    /// <summary>Same root, relative to the volume — the form the queue and the DB speak.</summary>
    public string RootRelativePath { get; }

    /// <summary>
    /// Absolute paths of everything this area created, in creation order. The run reports them so
    /// there is a written record of what the harness put on the operator's disks — and, by
    /// difference, of what the queue then moved away or sent to the Recycle Bin.
    /// </summary>
    public IReadOnlyList<string> CreatedPaths => _created;

    /// <summary>Absolute path of an entry inside the area.</summary>
    public string FullPath(string relative) =>
        relative.Length == 0 ? RootFullPath : Path.Combine(RootFullPath, relative);

    /// <summary>Volume-relative path of an entry inside the area.</summary>
    public string RelativePath(string relative) =>
        relative.Length == 0 ? RootRelativePath : ScanPath.Join(RootRelativePath, ScanPath.Normalize(relative));

    /// <summary>Creates a directory (and its parents) inside the area; returns its absolute path.</summary>
    public string CreateDirectory(string relative)
    {
        var full = FullPath(relative);
        Directory.CreateDirectory(full);
        _created.Add(full);
        return full;
    }

    /// <summary>
    /// Creates a file of exactly <paramref name="sizeBytes"/> bytes with deterministic,
    /// non-compressible-enough content derived from its own path, so a wrong file landing at the
    /// right path is still caught by the content comparison.
    /// </summary>
    public string CreateFile(string relative, long sizeBytes)
    {
        var full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var seed = relative.Aggregate(17, (acc, c) => unchecked(acc * 31 + c));
        var random = new Random(seed);
        var buffer = new byte[(int)Math.Min(ContentBufferSize, Math.Max(sizeBytes, 1))];
        random.NextBytes(buffer);

        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
        long written = 0;
        while (written < sizeBytes)
        {
            var chunk = (int)Math.Min(buffer.Length, sizeBytes - written);
            stream.Write(buffer, 0, chunk);
            written += chunk;
        }

        _created.Add(full);
        return full;
    }
}
