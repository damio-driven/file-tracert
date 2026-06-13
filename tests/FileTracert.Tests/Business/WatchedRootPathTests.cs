using FileTracert.Business.Setup;
using FluentAssertions;
using Xunit;

namespace FileTracert.Tests.Business;

public sealed class WatchedRootPathTests
{
    [Theory]
    [InlineData("Foto", "Foto")]
    [InlineData("/Foto/2024/", "Foto\\2024")]
    [InlineData("Foto\\2024\\", "Foto\\2024")]
    [InlineData("", "")]
    public void Normalize_strips_separators_and_unifies(string input, string expected) =>
        WatchedRootPath.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("..\\Foto")]
    [InlineData("Foto\\..\\..\\Windows")]
    [InlineData("C:\\Foto")]
    [InlineData("\\\\server\\share")]
    [InlineData("Foto\\..")]
    public void TryValidate_rejects_traversal_and_absolute(string input) =>
        WatchedRootPath.TryValidate(input, out _, out var error).Should().BeFalse(error);

    [Fact]
    public void TryValidate_accepts_clean_relative_path()
    {
        WatchedRootPath.TryValidate("/Foto/2024", out var normalized, out _).Should().BeTrue();
        normalized.Should().Be("Foto\\2024");
    }

    [Theory]
    [InlineData("Foto", "Foto", true)]
    [InlineData("Foto", "Foto\\2024", true)]
    [InlineData("Foto\\2024", "Foto", true)]
    [InlineData("Foto", "Video", false)]
    [InlineData("Foto", "Fotografie", false)]
    public void Conflicts_detects_nesting(string existing, string candidate, bool expected) =>
        WatchedRootPath.Conflicts(existing, candidate).Should().Be(expected);
}
