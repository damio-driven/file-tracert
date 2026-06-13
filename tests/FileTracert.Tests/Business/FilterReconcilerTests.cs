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
}
