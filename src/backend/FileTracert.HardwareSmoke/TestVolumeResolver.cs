namespace FileTracert.HardwareSmoke;

/// <summary>
/// Turns the configured <see cref="TestVolumeOptions"/> into <see cref="TestVolume"/> records by
/// resolving each folder onto the physical volume that hosts it. The scratch area is created here
/// (and only here): everything the harness writes afterwards lives inside it.
/// </summary>
public static class TestVolumeResolver
{
    public sealed record Result(IReadOnlyList<TestVolume> Volumes, IReadOnlyList<string> Failures);

    public static Result Resolve(HardwareSmokeOptions options, IVolumePathResolver resolver)
    {
        var volumes = new List<TestVolume>();
        var failures = new List<string>();

        foreach (var configured in options.TestVolumes)
        {
            var scratch = HarnessPaths.ScratchAreaOf(configured.Path, options.ScratchSubfolder);
            try
            {
                Directory.CreateDirectory(scratch);
                var resolved = resolver.Resolve(scratch);

                volumes.Add(new TestVolume(
                    Name: configured.Name.Trim(),
                    Kind: configured.Kind,
                    ConfiguredPath: Path.GetFullPath(configured.Path.Trim()),
                    VolumeGuid: resolved.VolumeGuid,
                    MountPoint: resolved.MountPoint,
                    ScratchFullPath: scratch,
                    ScratchRelativePath: resolved.RelativePath));
            }
            catch (Exception ex)
            {
                // Not silent (§9): the area is dropped from the run and the reason is reported to
                // the operator, who configured this path and needs to know it is unusable.
                failures.Add($"TestVolume '{configured.Name}' ({scratch}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new Result(volumes, failures);
    }
}
