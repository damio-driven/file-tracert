using FileTracert.Business.Filtering;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Step 18: an exclusion the delta INHERITS from a catalog row (the parent folder's
/// <c>ExcludedByScan</c>) must cover the paths under it exactly like one this delta found — and
/// must NOT be handed to the subtree pass, whose rows were stamped by the tick that found it.
/// Otherwise every tick that names a file inside a hidden folder would pay the subtree walk again
/// (31 ms per folder on the system volume) to write nothing.
/// </summary>
public sealed class ScanPerimeterInheritedTests
{
    private static readonly PerimeterVerdict Hidden = new(InactiveRoot: false, ExcludedByPath: false, ExcludedByAttributes: true);

    [Fact]
    public void An_inherited_exclusion_covers_its_subtree_but_is_not_a_root_to_walk()
    {
        var perimeter = new ScanPerimeter([string.Empty]);
        perimeter.ExcludeSubtree(@"Photos\Cache", Hidden, inherited: true);

        perimeter.IsExcluded(@"Photos\Cache\b.jpg").Should().BeTrue();
        perimeter.Covers(@"Photos\Cache\Sub\c.jpg").Should().BeFalse();
        perimeter.SkipVerdict(@"Photos\Cache\Sub\c.jpg").Should().Be(Hidden);
        perimeter.ExcludedSubtreeRoots.Should().BeEmpty("the rows below were stamped by the tick that saw the folder go hidden");
    }

    [Fact]
    public void A_folder_this_delta_excludes_itself_is_walked_even_if_a_row_already_said_so()
    {
        var perimeter = new ScanPerimeter([string.Empty]);
        perimeter.ExcludeSubtree(@"Photos\Cache", Hidden, inherited: true);
        perimeter.ExcludeSubtree(@"Photos\Cache", Hidden);

        perimeter.ExcludedSubtreeRoots.Should().ContainKey(@"Photos\Cache",
            "this delta named the folder: its verdict may have changed, so the subtree is re-stamped");
    }
}
