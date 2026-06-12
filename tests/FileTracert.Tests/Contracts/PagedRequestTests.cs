using FileTracert.Contracts.Paging;
using FluentAssertions;

namespace FileTracert.Tests.Contracts;

public sealed class PagedRequestTests
{
    [Fact]
    public void Caps_take_at_max()
    {
        var normalized = new PagedRequest(Skip: 0, Take: 10_000).Normalized();

        normalized.Take.Should().Be(PagedRequest.MaxTake);
    }

    [Fact]
    public void Non_positive_take_falls_back_to_default()
    {
        new PagedRequest(0, 0).Normalized().Take.Should().Be(PagedRequest.DefaultTake);
        new PagedRequest(0, -5).Normalized().Take.Should().Be(PagedRequest.DefaultTake);
    }

    [Fact]
    public void Negative_skip_is_clamped_to_zero()
    {
        new PagedRequest(-3, 20).Normalized().Skip.Should().Be(0);
    }

    [Fact]
    public void In_range_request_is_unchanged()
    {
        var normalized = new PagedRequest(40, 20, "name", true).Normalized();

        normalized.Skip.Should().Be(40);
        normalized.Take.Should().Be(20);
        normalized.SortBy.Should().Be("name");
        normalized.Desc.Should().BeTrue();
    }

    [Fact]
    public void Custom_cap_is_respected()
    {
        new PagedRequest(0, 90).Normalized(maxTake: 50).Take.Should().Be(50);
    }
}
