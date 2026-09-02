using FileTracert.Business.Filtering;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;
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

    /// <summary>
    /// The segment is matched between separators, which is the SQL side's frame — so the four
    /// boundary cases (first, last, middle, whole path) are one case, a segment never matches a
    /// prefix of a longer name, and a MULTI-PART segment matches the sequence instead of never
    /// matching at all. Both halves of the filter read one spelling of this since step 16.
    /// </summary>
    [Theory]
    [InlineData("AppData", @"AppData\x.jpg", true)]                 // first
    [InlineData("AppData", @"Users\Me\AppData", true)]              // last (the file NAME is a segment)
    [InlineData("AppData", @"Users\AppData\Local\x.jpg", true)]     // middle
    [InlineData("AppData", "AppData", true)]                        // the whole path
    [InlineData("AppData", @"AppDataStore\x.jpg", false)]           // never a prefix of a longer name
    [InlineData("AppData", @"MyAppData\x.jpg", false)]              // nor a suffix
    [InlineData("appdata", @"AppData\x.jpg", true)]                 // ASCII case folding, like LIKE
    [InlineData(@"AppData\Local", @"Users\AppData\Local\x.jpg", true)]
    [InlineData(@"AppData\Local", @"Users\AppData\Roaming\x.jpg", false)]
    [InlineData(@"Windows\", @"Windows\System32\x.dll", true)]      // §4's own spelling
    [InlineData(" Windows ", @"Windows\System32\x.dll", true)]
    public void An_excluded_segment_matches_between_separators(string segment, string path, bool excluded) =>
        FileFilter.IsPathExcluded(path, Filter(excludedSegments: [segment])).Should().Be(excluded);

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

    /// <summary>
    /// The perimeter answer names its rules since step 16, because they are undone by different
    /// owners: a path segment reconciliation can retract from the catalog alone, an attribute only
    /// another scan can. A bool made the first hostage to the second.
    /// </summary>
    [Fact]
    public void The_perimeter_says_which_of_its_two_rules_rejected_the_item()
    {
        var filter = Filter(excludedSegments: ["AppData"]);

        FileFilter.EvaluatePerimeter(@"Photos\a.jpg", FileAttributes.Normal, filter)
            .Should().Be(PerimeterVerdict.Inside, "inside the perimeter on both counts");
        FileFilter.EvaluatePerimeter(@"AppData\a.jpg", FileAttributes.Normal, filter)
            .Should().Be(new PerimeterVerdict(false, ExcludedByPath: true, ExcludedByAttributes: false));
        FileFilter.EvaluatePerimeter(@"Photos\a.jpg", FileAttributes.Hidden, filter)
            .Should().Be(new PerimeterVerdict(false, ExcludedByPath: false, ExcludedByAttributes: true));
    }

    /// <summary>
    /// Both rules rejecting it: BOTH are recorded, because the causes of 11h sum. Picking one — any
    /// one — means undoing it re-admits a row the other should still hold out: the content of a
    /// hidden folder walking back into the Catalog because an unrelated segment was dropped.
    /// </summary>
    [Fact]
    public void When_both_perimeter_rules_reject_it_both_are_recorded()
    {
        var verdict = FileFilter.EvaluatePerimeter(
            @"AppData\a.jpg", FileAttributes.Hidden, Filter(excludedSegments: ["AppData"]));

        verdict.ExcludedByPath.Should().BeTrue();
        verdict.ExcludedByAttributes.Should().BeTrue();
        verdict.Should().NotBe(PerimeterVerdict.Inside);

        var causes = new List<ScanSkipCause>();
        foreach (var cause in verdict)
        {
            causes.Add(cause);
        }

        causes.Should().BeEquivalentTo(
            [ScanSkipCause.ExcludedPath, ScanSkipCause.ExcludedAttributes],
            "the write side stages one skipped area per cause, and needs both of them");
    }

    /// <summary>An inside verdict enumerates nothing at all: there is no area to stage.</summary>
    [Fact]
    public void An_inside_verdict_carries_no_cause()
    {
        var causes = 0;
        foreach (var _ in PerimeterVerdict.Inside)
        {
            causes++;
        }

        causes.Should().Be(0);
        PerimeterVerdict.Inside.IsInside.Should().BeTrue();
        PerimeterVerdict.OutsideEveryRoot.IsInside.Should().BeFalse();
    }

    /// <summary>The old yes/no is the same question with the answer thrown away; it must stay so.</summary>
    [Theory]
    [InlineData(@"Photos\a.jpg", FileAttributes.Normal, true)]
    [InlineData(@"AppData\a.jpg", FileAttributes.Normal, false)]
    [InlineData(@"Photos\a.jpg", FileAttributes.Hidden, false)]
    [InlineData(@"AppData\a.jpg", FileAttributes.Hidden, false)]
    public void IsInsidePerimeter_agrees_with_the_cause(string path, FileAttributes attributes, bool inside) =>
        FileFilter.IsInsidePerimeter(path, attributes, Filter(excludedSegments: ["AppData"]))
            .Should().Be(inside);

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
        excluded.Add(@"Secret", new PerimeterVerdict(false, false, ExcludedByAttributes: true));

        excluded.Covers(path).Should().Be(covered);
    }

    [Fact]
    public void Excluded_subtree_set_ignores_the_volume_root()
    {
        var excluded = new ExcludedSubtrees();
        excluded.Add(string.Empty, new PerimeterVerdict(false, false, ExcludedByAttributes: true));

        excluded.Count.Should().Be(0);
        excluded.Covers(@"Anything\at\all").Should().BeFalse();
    }
}
