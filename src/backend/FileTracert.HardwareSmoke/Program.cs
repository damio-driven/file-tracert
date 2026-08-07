using FileTracert.Contracts.Platform;
using FileTracert.HardwareSmoke.Harness;
using FileTracert.HardwareSmoke.Report;
using FileTracert.HardwareSmoke.Scenarios;
using FileTracert.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke;

/// <summary>
/// Hardware harness entry point. Opt-in: does nothing unless <c>HardwareSmoke.Enabled</c> is true
/// and the guard-rails pass. Runs arrange → act → assert scenarios against real drives, through
/// the product's real queue services. Never part of CI.
///
/// Named explicitly instead of using top-level statements so this assembly does not emit a
/// <c>Program</c> type that collides with the Host's (the test project references both).
///
/// Exit codes: 0 = nothing to do / everything passed · 1 = at least one scenario FAILED ·
///             2 = refused (unsafe or unusable configuration).
/// </summary>
internal static class HarnessProgram
{
    private static async Task<int> Main()
    {
        SQLitePCL.Batteries.Init();

        var console = new HarnessConsole();

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var options = config.GetSection(HardwareSmokeOptions.SectionName).Get<HardwareSmokeOptions>()
                      ?? new HardwareSmokeOptions();

        if (!options.Enabled || options.TestVolumes.Count == 0)
        {
            console.Write("HardwareSmoke is disabled or has no TestVolumes configured. Nothing to do.");
            return 0;
        }

        // Bind the platform ports through the public DI wiring (the concrete Win32 types are internal).
        await using var platform = new ServiceCollection()
            .AddLogging()
            .AddPlatformServices()
            .BuildServiceProvider();

        var probe = platform.GetRequiredService<IVolumeProbe>();

        // ── guard-rails ──────────────────────────────────────────────────────

        var mainDbPath = ResolveMainDbPath(options);
        var production = ProductionRootsReader.Read(mainDbPath, probe);
        if (!production.CouldVerify)
        {
            console.Write($"REFUSING: the production database at '{mainDbPath}' exists but could not be read " +
                          $"({production.Error}) — cannot verify the configured areas are clear of catalogued data.");
            return 2;
        }

        var guard = HardwareSmokeGuard.Validate(options, production.RootPaths);
        if (!guard.Ok)
        {
            console.Write($"REFUSING: {guard.Reason}");
            return 2;
        }

        // ── resolve the configured areas onto real volumes ───────────────────

        var resolution = TestVolumeResolver.Resolve(options, new VolumePathResolver(probe));
        foreach (var failure in resolution.Failures)
            console.Write($"unusable test volume — {failure}");

        if (resolution.Volumes.Count == 0)
        {
            console.Write("REFUSING: none of the configured TestVolumes could be resolved onto a mounted volume.");
            return 2;
        }

        // The throwaway databases must not live inside an area the cleanup will wipe.
        var workRoot = Path.Combine(
            Path.GetTempPath(), "FileTracertHarness", $"run-{DateTime.Now:yyyyMMdd-HHmmss}");

        foreach (var volume in resolution.Volumes)
        {
            if (PathBoundary.Overlaps(workRoot, volume.ScratchFullPath))
            {
                console.Write($"REFUSING: the harness work root '{workRoot}' overlaps the scratch area of " +
                              $"'{volume.Name}' — set TEMP outside the configured test volumes.");
                return 2;
            }
        }

        // ── plan ─────────────────────────────────────────────────────────────

        var pairing = VolumePairing.Build(resolution.Volumes);
        var (scenarios, unknownNames) = ScenarioCatalog.Select(options.Scenarios);

        var notes = new List<string>(pairing.Notes);
        notes.AddRange(resolution.Failures.Select(f => $"skipped test volume — {f}"));
        foreach (var unknown in unknownNames)
            notes.Add($"the Scenarios filter names '{unknown}', which is not a known scenario.");
        if (!options.SemiAutomatic)
            notes.Add("SemiAutomatic=false: the physical unplug scenario did not run.");

        console.Write($"work root: {workRoot}");
        foreach (var volume in resolution.Volumes)
        {
            console.Write($"volume '{volume.Name}' ({volume.Kind}) → {volume.VolumeGuid} " +
                          $"at '{volume.MountPoint}', scratch '{volume.ScratchFullPath}'");
        }
        console.Write($"{pairing.Pairs.Count} pair(s) × {scenarios.Count} scenario(s) selected.");

        // ── run ──────────────────────────────────────────────────────────────

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };

        var runner = new HarnessRunner(options, scenarios, console);
        IReadOnlyList<ScenarioResult> results;
        try
        {
            results = await runner.RunAsync(pairing.Pairs, workRoot, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            console.Write("interrupted by the operator — cleaning up what was created so far.");
            ScratchCleanup.Run(resolution.Volumes, console);
            TryDeleteWorkRoot(workRoot, console);
            return 2;
        }

        Console.WriteLine(ReportPrinter.Render(results, notes));

        ScratchCleanup.Run(resolution.Volumes, console);
        TryDeleteWorkRoot(workRoot, console);

        return ReportPrinter.ExitCodeFor(results);
    }

    /// <summary>Same convention as the service: explicit override, else %LOCALAPPDATA%\FileTracert\filetracert.db.</summary>
    private static string ResolveMainDbPath(HardwareSmokeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.MainDatabasePath))
            return Path.GetFullPath(options.MainDatabasePath);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileTracert", "filetracert.db");
    }

    private static void TryDeleteWorkRoot(string workRoot, IHarnessConsole console)
    {
        try
        {
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
        catch (Exception ex)
        {
            // Not silent (§9): a locked throwaway database is left behind and the operator is told.
            console.Write($"could not delete the work root '{workRoot}': {ex.GetType().Name}: {ex.Message}");
        }
    }
}
