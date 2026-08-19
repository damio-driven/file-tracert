using FileTracert.Business.Filtering;
using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Business;

public class FileFilterTests
{
    private static EffectiveFilter Filter(
        IEnumerable<string>? extensions = null,
        IEnumerable<string>? excludedSegments = null) =>
        new(
            (extensions ?? []).ToHashSet(StringComparer.Ordinal),
            (excludedSegments ?? []).ToList());

    [Theory]
    [InlineData("photo.JPG", "jpg")]
    [InlineData("a.tar.gz", "gz")]
    [InlineData("noext", "")]
    [InlineData("trailingdot.", "")]
    public void GetExtension_returns_lowercased_no_dot(string name, string expected)
    {
        FileFilter.GetExtension(name).Should().Be(expected);
    }

    [Fact]
    public void ResolveCategory_falls_back_to_other()
    {
        var map = new Dictionary<string, FileCategory> { ["jpg"] = FileCategory.Image };

        FileFilter.ResolveCategory("jpg", map).Should().Be(FileCategory.Image);
        FileFilter.ResolveCategory("xyz", map).Should().Be(FileCategory.Other);
    }

    [Fact]
    public void Empty_allow_list_admits_any_extension()
    {
        var filter = Filter();
        FileFilter.ShouldIncludeFile("a\\b.zip", "zip", FileAttributes.Normal, filter).Should().BeTrue();
    }

    [Fact]
    public void Allow_list_restricts_to_listed_extensions()
    {
        var filter = Filter(extensions: ["jpg", "png"]);
        FileFilter.ShouldIncludeFile("a\\x.jpg", "jpg", FileAttributes.Normal, filter).Should().BeTrue();
        FileFilter.ShouldIncludeFile("a\\x.txt", "txt", FileAttributes.Normal, filter).Should().BeFalse();
    }

    [Fact]
    public void Excluded_path_segment_blocks_files_and_directories()
    {
        var filter = Filter(excludedSegments: ["Windows", "$Recycle.Bin"]);

        FileFilter.ShouldIncludeFile(@"Windows\System32\x.dll", "dll", FileAttributes.Normal, filter)
            .Should().BeFalse();
        FileFilter.ShouldIncludeDirectory(@"$Recycle.Bin", FileAttributes.Directory, filter)
            .Should().BeFalse();
        FileFilter.ShouldIncludeDirectory(@"Photos\2024", FileAttributes.Directory, filter)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(FileAttributes.System)]
    [InlineData(FileAttributes.Hidden)]
    public void System_and_hidden_are_excluded(FileAttributes attribute)
    {
        var filter = Filter();
        FileFilter.ShouldIncludeFile("a\\x.jpg", "jpg", attribute, filter).Should().BeFalse();
        FileFilter.ShouldIncludeDirectory("a", attribute | FileAttributes.Directory, filter).Should().BeFalse();
    }

    [Fact]
    public void Directories_are_not_filtered_by_extension()
    {
        var filter = Filter(extensions: ["jpg"]); // restrictive allow-list
        FileFilter.ShouldIncludeDirectory(@"Photos\backup.old", FileAttributes.Directory, filter)
            .Should().BeTrue();
    }

    [Fact]
    public void Builder_uses_app_settings_defaults()
    {
        var settings = new AppSettings
        {
            DefaultExtensionFilter = ["JPG", ".png"],
            ExcludedPaths = ["Windows"],
        };

        var filter = EffectiveFilterBuilder.Build(settings, filterOverrideJson: null);

        filter.AllowedExtensions.Should().BeEquivalentTo("jpg", "png");
        filter.ExcludedPathSegments.Should().Contain("Windows");
    }

    [Fact]
    public void Builder_applies_per_root_override()
    {
        var settings = new AppSettings { DefaultExtensionFilter = ["jpg"] };

        var filter = EffectiveFilterBuilder.Build(settings, """{ "extensions": ["mp4", "mkv"] }""");

        filter.AllowedExtensions.Should().BeEquivalentTo("mp4", "mkv");
    }

    [Fact]
    public void Builder_throws_on_malformed_override_for_the_caller_to_handle()
    {
        var settings = new AppSettings { DefaultExtensionFilter = ["jpg"] };

        // No silent swallow: a malformed override surfaces so the caller can log,
        // notify and fall back to defaults.
        var act = () => EffectiveFilterBuilder.Build(settings, "{ not valid json");

        act.Should().Throw<System.Text.Json.JsonException>();
    }

    // ── C16: the excluded-subtree set ─────────────────────────────────────────

    [Theory]
    [InlineData(@"Secret", true)]
    [InlineData(@"Secret\a.jpg", true)]
    [InlineData(@"secret\Deep\b.jpg", true)]        // case-insensitive, like every other path rule
    [InlineData(@"Secretive\a.jpg", false)]         // segment-aware: Secret is not a prefix of Secretive
    [InlineData(@"Secret.jpg", false)]              // a sibling FILE that merely starts with the name
    [InlineData(@"Photos\a.jpg", false)]
    public void Excluded_subtree_set_covers_descendants_and_only_whole_segments(string path, bool covered)
    {
        var excluded = new ExcludedSubtrees();
        excluded.Add(@"Secret");

        excluded.Covers(path).Should().Be(covered);
    }

    [Fact]
    public void Excluded_subtree_set_ignores_the_volume_root()
    {
        var excluded = new ExcludedSubtrees();
        excluded.Add(string.Empty);

        excluded.Count.Should().Be(0);
        excluded.Covers(@"Anything\at\all").Should().BeFalse();
    }
}
