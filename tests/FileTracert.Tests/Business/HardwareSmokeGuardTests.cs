using FileTracert.HardwareSmoke;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Guard-rail tests for the opt-in hardware-smoke harness. These are the safety net that keeps a
/// destructive dev tool from ever touching production data or the OS. Pure (no disk) except the
/// duplication test, which uses a throwaway temp tree.
/// </summary>
public sealed class HardwareSmokeGuardTests
{
    private static HardwareSmokeOptions Opts(string src, string tgt, string scratch, bool enabled = true) =>
        new() { Enabled = enabled, SourcePath = src, TargetPath = tgt, ScratchPath = scratch };

    // Three disjoint, non-system directories used as a valid baseline.
    private static (string src, string tgt, string scratch) SafeTriple()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ft-guard-{Guid.NewGuid():N}");
        return (Path.Combine(root, "src"), Path.Combine(root, "tgt"), Path.Combine(root, "scratch"));
    }

    [Fact]
    public void Disabled_is_denied_even_with_valid_paths()
    {
        var (s, t, w) = SafeTriple();
        var r = HardwareSmokeGuard.Validate(Opts(s, t, w, enabled: false), []);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("disabled");
    }

    [Fact]
    public void Empty_paths_are_denied()
    {
        HardwareSmokeGuard.Validate(Opts("", "", ""), []).Ok.Should().BeFalse();
        HardwareSmokeGuard.Validate(Opts(@"C:\a\src", "", @"C:\a\w"), []).Ok.Should().BeFalse();
    }

    [Fact]
    public void Valid_disjoint_non_system_paths_are_allowed()
    {
        var (s, t, w) = SafeTriple();
        var r = HardwareSmokeGuard.Validate(Opts(s, t, w), []);
        r.Ok.Should().BeTrue(r.Reason);
    }

    [Fact]
    public void Drive_root_is_denied()
    {
        var (_, t, w) = SafeTriple();
        var r = HardwareSmokeGuard.Validate(Opts(@"C:\", t, w), []);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("drive root");
    }

    [Fact]
    public void System_location_is_denied()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var (_, t, w) = SafeTriple();
        var r = HardwareSmokeGuard.Validate(Opts(Path.Combine(windows, "smoke"), t, w), []);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("system location");
    }

    [Fact]
    public void Path_overlapping_a_production_watched_root_is_denied()
    {
        var (s, t, w) = SafeTriple();
        // Target sits inside a production WatchedRoot → must be refused.
        var prodRoot = Path.Combine(Path.GetTempPath(), $"ft-prod-{Guid.NewGuid():N}");
        var t2 = Path.Combine(prodRoot, "inside");

        var r = HardwareSmokeGuard.Validate(Opts(s, t2, w), [prodRoot]);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("WatchedRoot");
    }

    [Fact]
    public void Production_root_nested_inside_a_configured_path_is_denied()
    {
        var (_, t, w) = SafeTriple();
        var source = Path.Combine(Path.GetTempPath(), $"ft-src-{Guid.NewGuid():N}");
        // A WatchedRoot lives UNDER the configured Source → overlap in the other direction.
        var prodRoot = Path.Combine(source, "catalogued");

        var r = HardwareSmokeGuard.Validate(Opts(source, t, w), [prodRoot]);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("WatchedRoot");
    }

    [Fact]
    public void Scratch_inside_source_is_denied_so_originals_are_never_touched()
    {
        var source = Path.Combine(Path.GetTempPath(), $"ft-src-{Guid.NewGuid():N}");
        var scratchInside = Path.Combine(source, "work");
        var target = Path.Combine(Path.GetTempPath(), $"ft-tgt-{Guid.NewGuid():N}");

        var r = HardwareSmokeGuard.Validate(Opts(source, target, scratchInside), []);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("ScratchPath overlaps SourcePath");
    }

    [Fact]
    public void Target_inside_source_is_denied()
    {
        var source = Path.Combine(Path.GetTempPath(), $"ft-src-{Guid.NewGuid():N}");
        var targetInside = Path.Combine(source, "out");
        var scratch = Path.Combine(Path.GetTempPath(), $"ft-w-{Guid.NewGuid():N}");

        var r = HardwareSmokeGuard.Validate(Opts(source, targetInside, scratch), []);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("TargetPath overlaps SourcePath");
    }

    // ── duplication operates on copies, never the originals ────────────────────

    [Fact]
    public void DuplicateSourceIntoScratch_copies_into_scratch_and_leaves_originals_intact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ft-dup-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "src");
        var scratch = Path.Combine(root, "scratch");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        var original = Path.Combine(source, "sub", "keep.txt");
        File.WriteAllText(original, "precious");
        Directory.CreateDirectory(scratch);

        try
        {
            var opts = Opts(source, Path.Combine(root, "tgt"), scratch);
            var workDir = HardwareSmokeRunner.DuplicateSourceIntoScratch(opts);

            // The duplicate exists under the work dir (inside scratch)…
            var duplicate = Path.Combine(workDir, "sub", "keep.txt");
            File.Exists(duplicate).Should().BeTrue();
            File.ReadAllText(duplicate).Should().Be("precious");
            workDir.Should().StartWith(Path.GetFullPath(scratch));

            // …and the original is untouched.
            File.Exists(original).Should().BeTrue();
            File.ReadAllText(original).Should().Be("precious");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
