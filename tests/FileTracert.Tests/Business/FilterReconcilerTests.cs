using FileTracert.Business.Filtering;
using FileTracert.Business.Setup;
using FluentAssertions;
using Xunit;

namespace FileTracert.Tests.Business;

public sealed class FilterReconcilerTests
{
    private static EffectiveFilter Filter(params string[] ext) =>
        new(ext.ToHashSet(StringComparer.Ordinal), []);

    [Fact]
    public void Widened_when_extension_added() =>
        FilterReconciler.FilterWidened(Filter("jpg"), Filter("jpg", "png")).Should().BeTrue();

    [Fact]
    public void Not_widened_when_extension_removed() =>
        FilterReconciler.FilterWidened(Filter("jpg", "png"), Filter("jpg")).Should().BeFalse();

    [Fact]
    public void Widened_when_new_filter_allows_all_types() =>
        FilterReconciler.FilterWidened(Filter("jpg"), Filter()).Should().BeTrue();

    [Fact]
    public void Not_widened_when_old_already_allowed_all() =>
        FilterReconciler.FilterWidened(Filter(), Filter("jpg")).Should().BeFalse();

    [Fact]
    public void Not_widened_when_unchanged() =>
        FilterReconciler.FilterWidened(Filter("jpg"), Filter("jpg")).Should().BeFalse();

    private static EffectiveFilter Excluding(params string[] segments) =>
        new(new HashSet<string>(StringComparer.Ordinal), segments);

    // An excluded path segment is a PERIMETER rule: nothing under it was ever indexed, so
    // dropping one needs a scan exactly as much as allowing a new file type does.
    [Fact]
    public void Widened_when_an_excluded_path_segment_is_dropped() =>
        FilterReconciler.FilterWidened(Excluding("AppData", "Windows"), Excluding("Windows"))
            .Should().BeTrue();

    [Fact]
    public void Not_widened_when_an_excluded_path_segment_is_added() =>
        FilterReconciler.FilterWidened(Excluding("Windows"), Excluding("Windows", "AppData"))
            .Should().BeFalse();

    /// <summary>
    /// The comparison folds case the way the two MATCHING halves do — ASCII only, because SQLite's
    /// <c>LIKE</c> can do no more and <c>FileFilter.IsPathExcluded</c> deliberately matches it. To
    /// both of them <c>Über</c> and <c>über</c> are different segments: the rows under the first are
    /// re-included by reconciliation with no scan, and the rows under the second were never indexed,
    /// which is precisely a widening. Comparing with <c>OrdinalIgnoreCase</c> answered "nothing was
    /// relaxed" and the screen told the user no scan was needed for work only a scan can do.
    /// </summary>
    [Fact]
    public void A_non_ascii_case_variant_is_a_different_segment_to_both_halves_so_it_is_a_widening() =>
        FilterReconciler.FilterWidened(Excluding("Über"), Excluding("über")).Should().BeTrue();

    [Fact]
    public void Path_exclusion_comparison_ignores_case() =>
        FilterReconciler.FilterWidened(Excluding("AppData"), Excluding("appdata")).Should().BeFalse();

    /// <summary>
    /// Re-spelling a segment is not a widening, and this comparison gets that for free now that
    /// <see cref="EffectiveFilter"/> normalizes once for every reader. Raw string comparison used to
    /// call <c>Windows\ → Windows</c> a widening and send the user off to run a full scan for a
    /// change that means nothing.
    /// </summary>
    [Theory]
    [InlineData(@"Windows\", "Windows")]
    [InlineData("Windows", @"\Windows")]
    [InlineData("Windows", " Windows ")]
    [InlineData("Windows", "Windows/")]
    public void Respelling_a_segment_is_not_a_widening(string before, string after) =>
        FilterReconciler.FilterWidened(Excluding(before), Excluding(after)).Should().BeFalse();
}
