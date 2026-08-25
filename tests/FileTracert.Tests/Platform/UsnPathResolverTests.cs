using FileTracert.Contracts.Platform;
using FluentAssertions;

namespace FileTracert.Tests.Platform;

public class UsnPathResolverTests
{
    private const ulong Root = 5;

    private static UsnPathResolver Build(
        Dictionary<ulong, FrnNode> map,
        Func<ulong, string?>? knownPath = null) =>
        new(map, Root, knownPath);

    [Fact]
    public void Root_resolves_to_empty_string()
    {
        var resolver = Build(new Dictionary<ulong, FrnNode>());

        resolver.TryResolve(Root, out var path).Should().BeTrue();
        path.Should().BeEmpty();
    }

    [Fact]
    public void File_directly_under_root_resolves_to_its_name()
    {
        var map = new Dictionary<ulong, FrnNode>
        {
            [10] = new FrnNode("readme.txt", Root, IsDirectory: false)
        };

        Build(map).TryResolve(10, out var path).Should().BeTrue();
        path.Should().Be("readme.txt");
    }

    [Fact]
    public void Nested_path_is_reconstructed_root_first()
    {
        var map = new Dictionary<ulong, FrnNode>
        {
            [10] = new FrnNode("Windows", Root, IsDirectory: true),
            [20] = new FrnNode("System32", 10, IsDirectory: true),
            [30] = new FrnNode("drivers", 20, IsDirectory: true),
            [40] = new FrnNode("etc", 30, IsDirectory: true),
            [50] = new FrnNode("hosts", 40, IsDirectory: false)
        };

        Build(map).TryResolve(50, out var path).Should().BeTrue();
        path.Should().Be(@"Windows\System32\drivers\etc\hosts");
    }

    [Fact]
    public void Self_referential_node_is_treated_as_root_boundary()
    {
        // FRN 5 with parent 5 reached via a child: child name only.
        var map = new Dictionary<ulong, FrnNode>
        {
            [5] = new FrnNode(".", 5, IsDirectory: true),
            [10] = new FrnNode("file", 5, IsDirectory: false)
        };

        Build(map).TryResolve(10, out var path).Should().BeTrue();
        path.Should().Be("file");
    }

    [Fact]
    public void Missing_parent_is_reported_as_unresolved()
    {
        var map = new Dictionary<ulong, FrnNode>
        {
            [10] = new FrnNode("orphan.txt", 999, IsDirectory: false)
        };

        Build(map).TryResolve(10, out var path).Should().BeFalse();
        path.Should().BeEmpty();
    }

    [Fact]
    public void Cyclic_chain_is_capped_and_reported_unresolved()
    {
        var map = new Dictionary<ulong, FrnNode>
        {
            [50] = new FrnNode("a", 51, IsDirectory: true),
            [51] = new FrnNode("b", 50, IsDirectory: true)
        };

        Build(map).TryResolve(50, out _).Should().BeFalse();
    }

    [Fact]
    public void Known_path_fallback_supplies_parents_outside_the_map()
    {
        var map = new Dictionary<ulong, FrnNode>
        {
            [60] = new FrnNode("photo.jpg", 70, IsDirectory: false)
        };

        // 70 ("Pictures") is not in the delta map; the catalog already places it.
        Build(map, frn => frn == 70 ? "Pictures" : null)
            .TryResolve(60, out var path).Should().BeTrue();
        path.Should().Be(@"Pictures\photo.jpg");
    }

    /// <summary>
    /// A whole chain of new directories can hang off one known ancestor: the walk keeps
    /// collecting names until the fallback answers, then stitches the two halves together.
    /// </summary>
    [Fact]
    public void Known_path_fallback_prefixes_a_chain_found_inside_the_map()
    {
        var map = new Dictionary<ulong, FrnNode>
        {
            [60] = new FrnNode("shot.raw", 61, IsDirectory: false),
            [61] = new FrnNode("Album", 62, IsDirectory: true),
            [62] = new FrnNode("2026", 70, IsDirectory: true),
        };

        Build(map, frn => frn == 70 ? "Pictures" : null)
            .TryResolve(60, out var path).Should().BeTrue();
        path.Should().Be(@"Pictures\2026\Album\shot.raw");
    }

    /// <summary>
    /// The empty path is the volume root, and it is a real answer, not "unknown": a directory
    /// row whose MaterializedPath is "" must not turn its children into a rooted path.
    /// </summary>
    [Fact]
    public void Known_path_fallback_can_answer_with_the_volume_root()
    {
        var map = new Dictionary<ulong, FrnNode>
        {
            [60] = new FrnNode("top.jpg", 70, IsDirectory: false)
        };

        Build(map, frn => frn == 70 ? string.Empty : null)
            .TryResolve(60, out var path).Should().BeTrue();
        path.Should().Be("top.jpg");
    }

    [Fact]
    public void Fallback_returning_null_keeps_entry_unresolved()
    {
        var map = new Dictionary<ulong, FrnNode>
        {
            [60] = new FrnNode("x", 70, IsDirectory: false)
        };

        Build(map, _ => null).TryResolve(60, out _).Should().BeFalse();
    }
}
