using FileTracert.Business.Filtering;
using FileTracert.HardwareSmoke.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>Everything a scenario needs: its fixtures, the real services, and the assert sink.</summary>
public sealed class ScenarioContext
{
    public ScenarioContext(
        HardwareSmokeOptions options,
        VolumePair pair,
        ScenarioEnvironment environment,
        QueueDriver queue,
        FixtureArea source,
        FixtureArea target,
        IHarnessConsole console,
        CancellationToken ct)
    {
        Options = options;
        Pair = pair;
        Env = environment;
        Queue = queue;
        Source = source;
        Target = target;
        Console = console;
        Ct = ct;
    }

    public HardwareSmokeOptions Options { get; }
    public VolumePair Pair { get; }
    public ScenarioEnvironment Env { get; }
    public QueueDriver Queue { get; }
    public ScenarioAssertions Assert { get; } = new();
    public IHarnessConsole Console { get; }
    public CancellationToken Ct { get; }

    /// <summary>Fixture area the operations move things OUT of.</summary>
    public FixtureArea Source { get; }

    /// <summary>Fixture area the operations move things INTO.</summary>
    public FixtureArea Target { get; }

    public int SourceVolumeId => Env.SourceVolumeId;
    public int TargetVolumeId => Env.TargetVolumeId;

    public TimeSpan Timeout => TimeSpan.FromSeconds(Options.ScenarioTimeoutSeconds);

    public long LargeFileBytes => (long)Math.Max(1, Options.LargeFileMegabytes) * 1024L * 1024L;

    public void Log(string line) => Console.Write($"    {line}");

    /// <summary>Indexes an arranged fixture area into the catalog through the real filter.</summary>
    public Task<CatalogArranger.IndexResult> IndexAsync(FixtureArea area, int volumeId, EffectiveFilter filter) =>
        Env.WithScopeAsync(sp => sp.GetRequiredService<CatalogArranger>()
            .IndexAsync(volumeId, area.Volume.MountPoint, area.RootRelativePath, filter, Ct));

    /// <summary>Indexes the source area — the usual arrange step.</summary>
    public Task<CatalogArranger.IndexResult> IndexSourceAsync(EffectiveFilter filter) =>
        IndexAsync(Source, SourceVolumeId, filter);
}
