namespace FileTracert.HardwareSmoke;

/// <summary>A source→target combination a scenario can run on.</summary>
public sealed record VolumePair(TestVolume Source, TestVolume Target)
{
    /// <summary>
    /// True when source and target are genuinely different physical volumes. Two configured
    /// areas that happen to live on the same drive are NOT cross-volume, however differently
    /// they are named — the volume GUID is the identity (CLAUDE.md §4).
    /// </summary>
    public bool IsCrossVolume =>
        !string.Equals(Source.VolumeGuid, Target.VolumeGuid, StringComparison.OrdinalIgnoreCase);

    public string Label => IsCrossVolume
        ? $"{Source.Name} → {Target.Name}"
        : $"{Source.Name} (intra)";
}

/// <summary>The pairs to run plus the human-readable reasons some combinations were dropped.</summary>
public sealed record PairingResult(IReadOnlyList<VolumePair> Pairs, IReadOnlyList<string> Notes);

/// <summary>
/// Turns the configured test areas into the source→target pairs the scenarios run on. Pure, so
/// the selection is unit-testable without touching a disk.
///
/// Two families:
///   • one <b>intra-volume</b> pair per area (source and target the same volume) — the fast
///     metadata-only path;
///   • one <b>cross-volume</b> pair per unordered combination of areas on <em>different</em>
///     volumes. Unordered: A→B and B→A exercise the same code path with the roles swapped, so
///     running both only doubles the wall clock. Every kind combination the config allows
///     (internal→internal, internal→external, external→external) is still covered.
/// </summary>
public static class VolumePairing
{
    public static PairingResult Build(IReadOnlyList<TestVolume> volumes)
    {
        var pairs = new List<VolumePair>();
        var notes = new List<string>();

        foreach (var volume in volumes)
            pairs.Add(new VolumePair(volume, volume));

        for (int i = 0; i < volumes.Count; i++)
        {
            for (int j = i + 1; j < volumes.Count; j++)
            {
                var a = volumes[i];
                var b = volumes[j];

                if (string.Equals(a.VolumeGuid, b.VolumeGuid, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(
                        $"'{a.Name}' and '{b.Name}' are on the same physical volume " +
                        $"({a.VolumeGuid}) — no cross-volume pair generated for them.");
                    continue;
                }

                pairs.Add(new VolumePair(a, b));
            }
        }

        if (!pairs.Any(p => p.IsCrossVolume))
        {
            notes.Add(
                "No cross-volume pair could be generated: configure at least two TestVolumes " +
                "on different physical drives to exercise the copy→verify→finalize path.");
        }

        return new PairingResult(pairs, notes);
    }
}
