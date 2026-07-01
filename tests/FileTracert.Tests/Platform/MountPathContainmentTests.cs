using FileTracert.Platform;
using FluentAssertions;

namespace FileTracert.Tests.Platform;

/// <summary>
/// Pure (no-disk) tests for <see cref="Win32FileMover.ResolveWithinMount"/> — the write-path
/// containment guard. Reproduces the path-traversal hole: a rooted or <c>..</c> relative path
/// must never resolve outside the volume mount, because the mover runs in the elevated service.
/// </summary>
public sealed class MountPathContainmentTests
{
    private const string Mount = @"C:\Mount\";

    [Theory]
    [InlineData(@"\x")]              // leading separator → Path.Combine discards the mount
    [InlineData(@"/x")]             // forward-slash variant
    [InlineData(@"..\..\x")]        // parent traversal escapes the volume
    [InlineData(@"sub\..\..\x")]   // traversal buried mid-path
    [InlineData(@"C:\altro")]       // drive-qualified absolute path
    [InlineData(@"D:relative")]     // drive-relative path
    public void ResolveWithinMount_rejects_escaping_paths(string relativePath)
    {
        var act = () => Win32FileMover.ResolveWithinMount(Mount, relativePath);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ResolveWithinMount_accepts_legit_relative_path()
    {
        var full = Win32FileMover.ResolveWithinMount(Mount, @"sub\dir\file.txt");

        full.Should().Be(@"C:\Mount\sub\dir\file.txt");
    }

    [Fact]
    public void ResolveWithinMount_accepts_volume_root()
    {
        var full = Win32FileMover.ResolveWithinMount(Mount, string.Empty);

        full.Should().Be(@"C:\Mount\");
    }

    [Fact]
    public void ResolveWithinMount_does_not_accept_sibling_prefix_match()
    {
        // "C:\Mount2" must not pass containment for mount "C:\Mount" (boundary separator).
        var act = () => Win32FileMover.ResolveWithinMount(@"C:\Mount", @"..\Mount2\x");

        act.Should().Throw<InvalidOperationException>();
    }
}
