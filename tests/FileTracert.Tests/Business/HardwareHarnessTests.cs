using FileTracert.HardwareSmoke;
using FileTracert.HardwareSmoke.Harness;
using FileTracert.HardwareSmoke.Report;
using FileTracert.HardwareSmoke.Scenarios;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Tests for the harness's own machinery — the parts that decide WHAT runs, WHERE it writes and
/// WHAT the run reports. The scenarios themselves are hardware-bound and deliberately live outside
/// <c>dotnet test</c>; everything that can be pinned down without a real drive is pinned here.
/// </summary>
public sealed class HardwareHarnessTests : IDisposable
{
    private readonly List<string> _created = [];

    private string TempDir(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ft-harness-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _created.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _created)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // Leftover temp folder: harmless, never worth failing a test over.
            }
        }
    }

    private static TestVolume Volume(string name, string guid, TestVolumeKind kind = TestVolumeKind.Internal,
        string mount = @"X:\", string scratchFull = @"X:\safe\FileTracertHarness",
        string scratchRelative = @"safe\FileTracertHarness") =>
        new(name, kind, @"X:\safe", guid, mount, scratchFull, scratchRelative);

    // ── volume-pair generation ────────────────────────────────────────────────

    [Fact]
    public void Each_volume_yields_an_intra_pair()
    {
        var result = VolumePairing.Build([Volume("a", "{A}"), Volume("b", "{B}")]);

        result.Pairs.Where(p => !p.IsCrossVolume)
            .Select(p => p.Source.Name)
            .Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void Volumes_on_different_drives_yield_one_cross_pair_each_combination()
    {
        var result = VolumePairing.Build([
            Volume("internal-a", "{A}"),
            Volume("internal-b", "{B}"),
            Volume("external", "{C}", TestVolumeKind.External),
        ]);

        var cross = result.Pairs.Where(p => p.IsCrossVolume).ToList();

        cross.Should().HaveCount(3);
        cross.Select(p => $"{p.Source.Name}>{p.Target.Name}")
            .Should().BeEquivalentTo(["internal-a>internal-b", "internal-a>external", "internal-b>external"]);
    }

    [Fact]
    public void Two_areas_on_the_same_physical_volume_produce_no_cross_pair_and_say_why()
    {
        var result = VolumePairing.Build([Volume("first", "{SAME}"), Volume("second", "{SAME}")]);

        result.Pairs.Should().NotContain(p => p.IsCrossVolume);
        result.Notes.Should().Contain(n => n.Contains("same physical volume"));
    }

    [Fact]
    public void A_single_volume_is_reported_as_unable_to_test_the_cross_volume_path()
    {
        var result = VolumePairing.Build([Volume("only", "{A}")]);

        result.Notes.Should().Contain(n => n.Contains("No cross-volume pair"));
    }

    // ── scenario selection ────────────────────────────────────────────────────

    [Fact]
    public void Star_filter_selects_every_scenario()
    {
        var (selected, unknown) = ScenarioCatalog.Select(["*"]);

        selected.Should().HaveCount(ScenarioCatalog.All().Count);
        unknown.Should().BeEmpty();
    }

    [Fact]
    public void Named_filter_selects_only_those_scenarios_and_reports_typos()
    {
        var (selected, unknown) = ScenarioCatalog.Select(["move-file-cross", "not-a-scenario"]);

        selected.Select(s => s.Name).Should().BeEquivalentTo(["move-file-cross"]);
        unknown.Should().BeEquivalentTo(["not-a-scenario"]);
    }

    [Fact]
    public void Scenario_names_are_unique()
    {
        var names = ScenarioCatalog.All().Select(s => s.Name).ToList();

        names.Should().OnlyHaveUniqueItems();
    }

    // ── applicability ─────────────────────────────────────────────────────────

    [Fact]
    public void Cross_volume_scenarios_do_not_run_on_an_intra_pair()
    {
        var options = new HardwareSmokeOptions { Enabled = true };
        var volume = Volume("a", "{A}");
        var intra = new VolumePair(volume, volume);

        new MoveFileCrossVolumeScenario().AppliesTo(intra, options).Should().BeFalse();
        new MoveFileIntraVolumeScenario().AppliesTo(intra, options).Should().BeTrue();
    }

    [Fact]
    public void The_unplug_scenario_needs_both_the_opt_in_and_an_external_target()
    {
        var scenario = new OfflineUnplugScenario();
        var toInternal = new VolumePair(Volume("a", "{A}"), Volume("b", "{B}"));
        var toExternal = new VolumePair(Volume("a", "{A}"), Volume("usb", "{C}", TestVolumeKind.External));

        scenario.AppliesTo(toExternal, new HardwareSmokeOptions { SemiAutomatic = false }).Should().BeFalse();
        scenario.AppliesTo(toInternal, new HardwareSmokeOptions { SemiAutomatic = true }).Should().BeFalse();
        scenario.AppliesTo(toExternal, new HardwareSmokeOptions { SemiAutomatic = true }).Should().BeTrue();
    }

    // ── the harness only ever writes inside its own scratch area ──────────────

    [Fact]
    public void Fixtures_are_created_under_the_scratch_area_and_leave_pre_existing_content_alone()
    {
        var volumeRoot = TempDir("vol");
        var precious = Path.Combine(volumeRoot, "user-file.jpg");
        File.WriteAllText(precious, "precious");

        var scratch = HarnessPaths.ScratchAreaOf(volumeRoot, "FileTracertHarness");
        var volume = Volume("a", "{A}", scratchFull: scratch, scratchRelative: @"whatever\FileTracertHarness");

        var area = new FixtureArea(volume, "some-scenario", "source");
        var created = area.CreateFile(@"album\photo.jpg", 1024);

        // …the fixture landed inside the scratch area…
        created.Should().StartWith(scratch);
        new FileInfo(created).Length.Should().Be(1024);

        // …and the user's own file, one level up, was never seen.
        File.Exists(precious).Should().BeTrue();
        File.ReadAllText(precious).Should().Be("precious");

        // Deleting only the scratch area is enough to undo everything the harness did.
        Directory.Delete(scratch, recursive: true);
        File.Exists(precious).Should().BeTrue();
    }

    [Fact]
    public void A_fixture_area_reports_the_volume_relative_path_the_queue_speaks()
    {
        var volume = Volume("a", "{A}",
            scratchFull: HarnessPaths.ScratchAreaOf(TempDir("vol"), "FileTracertHarness"),
            scratchRelative: @"safe\FileTracertHarness");
        var area = new FixtureArea(volume, "scenario", "source");

        area.RootRelativePath.Should().Be(@"safe\FileTracertHarness\scenario\source");
        area.RelativePath(@"album\photo.jpg").Should().Be(@"safe\FileTracertHarness\scenario\source\album\photo.jpg");
    }

    // ── the report drives the exit code ───────────────────────────────────────

    [Fact]
    public void A_failing_scenario_makes_the_run_exit_non_zero()
    {
        IReadOnlyList<ScenarioResult> results =
        [
            ScenarioResult.Pass("ok-one", "a → b", TimeSpan.FromSeconds(1)),
            ScenarioResult.Fail("broken", "a → b", ["target file missing"], TimeSpan.FromSeconds(2)),
        ];

        ReportPrinter.ExitCodeFor(results).Should().NotBe(0);
        ReportPrinter.Render(results, []).Should().Contain("target file missing").And.Contain("FAIL 1");
    }

    [Fact]
    public void Only_passes_and_skips_exit_zero()
    {
        IReadOnlyList<ScenarioResult> results =
        [
            ScenarioResult.Pass("ok-one", "a → b", TimeSpan.FromSeconds(1)),
            ScenarioResult.Skipped("inconclusive", "a → b", "copy finished too fast", TimeSpan.Zero),
        ];

        ReportPrinter.ExitCodeFor(results).Should().Be(0);
        ReportPrinter.Render(results, []).Should().Contain("copy finished too fast");
    }

    // ── the assertion sink reports everything that is wrong, not just the first ─

    [Fact]
    public void Assertions_accumulate_so_one_run_reports_every_problem()
    {
        var asserts = new ScenarioAssertions();

        asserts.True(false, "first problem");
        asserts.Equal(1, 2, "second problem");
        asserts.FileExists(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"), "third problem");

        asserts.AnyFailed.Should().BeTrue();
        asserts.Failures.Should().HaveCount(3);
        asserts.Failures[1].Should().Contain("expected '1', got '2'");
    }

    [Fact]
    public void A_leftover_partial_is_always_a_failure()
    {
        var directory = TempDir("partials");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "nested", "x.bin" + ScenarioAssertions.PartialSuffix), "half");

        var asserts = new ScenarioAssertions();
        asserts.NoPartialsUnder(directory, "after the job");

        asserts.Failures.Should().ContainSingle().Which.Should().Contain(ScenarioAssertions.PartialSuffix);
    }
}
