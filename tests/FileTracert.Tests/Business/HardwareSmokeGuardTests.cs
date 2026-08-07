using FileTracert.HardwareSmoke;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Guard-rail tests for the opt-in hardware harness. These are the safety net that keeps a
/// destructive dev tool from ever touching production data or the OS. The guard probes the
/// filesystem for existence, so the fixtures create real (throwaway) temp folders.
/// </summary>
public sealed class HardwareSmokeGuardTests : IDisposable
{
    private readonly List<string> _created = [];

    private string TempDir(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ft-guard-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _created.Add(path);
        return path;
    }

    private static HardwareSmokeOptions Opts(bool enabled, params TestVolumeOptions[] volumes) =>
        new() { Enabled = enabled, TestVolumes = [.. volumes] };

    private static TestVolumeOptions Vol(string name, string path, TestVolumeKind kind = TestVolumeKind.Internal) =>
        new() { Name = name, Path = path, Kind = kind };

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
                // Leftover temp folder in the OS temp dir: harmless, and never worth failing a test.
            }
        }
    }

    // ── the master switch ─────────────────────────────────────────────────────

    [Fact]
    public void Disabled_is_denied_even_with_valid_paths()
    {
        var result = HardwareSmokeGuard.Validate(Opts(false, Vol("a", TempDir("a"))), []);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("disabled");
    }

    [Fact]
    public void No_test_volumes_is_denied()
    {
        var result = HardwareSmokeGuard.Validate(Opts(true), []);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("No TestVolumes");
    }

    [Fact]
    public void Valid_disjoint_non_system_paths_are_allowed()
    {
        var result = HardwareSmokeGuard.Validate(
            Opts(true, Vol("a", TempDir("a")), Vol("b", TempDir("b"), TestVolumeKind.External)), []);

        result.Ok.Should().BeTrue(result.Reason);
    }

    // ── configuration hygiene ─────────────────────────────────────────────────

    [Fact]
    public void Missing_path_is_denied()
    {
        var result = HardwareSmokeGuard.Validate(Opts(true, Vol("a", "")), []);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("no Path");
    }

    [Fact]
    public void Nonexistent_path_is_denied_so_a_typo_never_creates_a_work_area()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"ft-does-not-exist-{Guid.NewGuid():N}");

        var result = HardwareSmokeGuard.Validate(Opts(true, Vol("a", missing)), []);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("does not exist");
    }

    [Fact]
    public void Duplicate_volume_names_are_denied()
    {
        var result = HardwareSmokeGuard.Validate(
            Opts(true, Vol("same", TempDir("a")), Vol("same", TempDir("b"))), []);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("Duplicate");
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(@"nested\folder")]
    [InlineData("C:")]
    public void Unsafe_scratch_subfolder_is_denied(string subfolder)
    {
        var options = Opts(true, Vol("a", TempDir("a")));
        options.ScratchSubfolder = subfolder;

        var result = HardwareSmokeGuard.Validate(options, []);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("ScratchSubfolder");
    }

    // ── never the OS ──────────────────────────────────────────────────────────

    [Fact]
    public void Drive_root_is_denied()
    {
        var result = HardwareSmokeGuard.Validate(Opts(true, Vol("a", @"C:\")), []);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("drive root");
    }

    [Fact]
    public void System_location_is_denied()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var result = HardwareSmokeGuard.Validate(Opts(true, Vol("a", Path.Combine(windows, "System32"))), []);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("system location");
    }

    // ── never production data ─────────────────────────────────────────────────

    [Fact]
    public void Path_inside_a_production_watched_root_is_denied()
    {
        var prodRoot = TempDir("prod");
        var inside = Path.Combine(prodRoot, "inside");
        Directory.CreateDirectory(inside);

        var result = HardwareSmokeGuard.Validate(Opts(true, Vol("a", inside)), [prodRoot]);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("WatchedRoot");
    }

    [Fact]
    public void Production_root_nested_inside_a_configured_path_is_denied()
    {
        var configured = TempDir("configured");
        var prodRoot = Path.Combine(configured, "catalogued");

        var result = HardwareSmokeGuard.Validate(Opts(true, Vol("a", configured)), [prodRoot]);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("WatchedRoot");
    }

    // ── the areas must not collide ────────────────────────────────────────────

    [Fact]
    public void Overlapping_test_volumes_are_denied()
    {
        var outer = TempDir("outer");
        var inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);

        var result = HardwareSmokeGuard.Validate(Opts(true, Vol("outer", outer), Vol("inner", inner)), []);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("overlap");
    }
}
