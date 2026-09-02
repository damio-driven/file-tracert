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
    /// The normalization lives on the VALUE, so there is no way to hold an
    /// <see cref="EffectiveFilter"/> whose two readers — the scan in memory and the reconciler in
    /// SQL — would disagree about a segment. A <c>with</c> expression is the other door, and it goes
    /// through the same accessor.
    /// </summary>
    [Fact]
    public void An_effective_filter_cannot_hold_an_unnormalized_segment()
    {
        var built = Filter(excludedSegments: [@"Windows\", " AppData ", @"\Temp", "", "   ", "windows"]);

        // Trimmed, folded, emptied and de-duplicated — once, for both halves.
        built.ExcludedPathSegments.Should().Equal("Windows", "AppData", "Temp");

        (built with { ExcludedPathSegments = [@"Program Files\"] }).ExcludedPathSegments
            .Should().Equal("Program Files");
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
    [InlineData("Über", @"Über\x.jpg", true)]                       // identical is identical
    [InlineData("Über", @"über\x.jpg", false)]                      // …but the fold stops at ASCII
    public void An_excluded_segment_matches_between_separators(string segment, string path, bool excluded) =>
        FileFilter.IsPathExcluded(path, Filter(excludedSegments: [segment])).Should().Be(excluded);

    /// <summary>
    /// E7 territory, and the doc on <see cref="FileFilter.IsPathExcluded"/> claims it outright: a
    /// scan asks this once per ENUMERATED ITEM — millions on a real volume — so it must not
    /// allocate. It did. <c>EffectiveFilter.ExcludedPathSegments</c> is typed as an INTERFACE, so
    /// <c>foreach</c> went through <c>IEnumerable&lt;string&gt;.GetEnumerator()</c> and boxed
    /// <c>List&lt;string&gt;.Enumerator</c> on every call: 40 bytes a call, ~30 MB of garbage per
    /// scan of the installed catalog, for a method whose whole point was that it builds nothing.
    ///
    /// <para>Measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/> — a counter, not a
    /// stopwatch, which is the only unit that makes zero a stable assertion on any machine, the
    /// same argument <c>RootsBySpecificityTests</c> makes.</para>
    /// </summary>
    [Fact]
    public void Deciding_the_path_half_of_a_million_items_allocates_nothing()
    {
        var filter = Filter(excludedSegments: ["Windows", "Program Files", "$Recycle.Bin", "AppData"]);
        var paths = new[]
        {
            @"Media\Foto\a.jpg", @"Windows\System32\b.dll", @"Users\Me\AppData\Local\c.tmp",
            @"Documenti\d.pdf", @"Program Files\App\e.exe", @"Altro\Sotto\f.txt",
        };

        // Warm-up: first-call JIT is not what is being measured.
        foreach (var p in paths) FileFilter.IsPathExcluded(p, filter);

        const int iterations = 200_000;
        var before = GC.GetAllocatedBytesForCurrentThread();

        var excluded = false;
        for (var i = 0; i < iterations; i++)
        {
            excluded = FileFilter.IsPathExcluded(paths[i % paths.Length], filter);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Asserted so the loop cannot be optimised away, and so an allocation-free method that
        // stopped answering correctly would still be caught here.
        excluded.Should().Be(
            FileFilter.IsPathExcluded(paths[(iterations - 1) % paths.Length], filter),
            "the last iteration still has to give the right answer");
        allocated.Should().Be(0,
            "{0} calls must not allocate — the interface-typed foreach boxed a list enumerator " +
            "on every one of them", iterations);
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

    /// <summary>
    /// The view the delta walks must not be a door back into the set. Handing out the live
    /// dictionary made <c>Roots</c> castable straight to <see cref="IDictionary{TKey,TValue}"/> and
    /// writable through it — the same fragility that was taken out of <c>Add</c>, which unions
    /// rather than replaces so that no caller's habit can silently drop a cause.
    /// </summary>
    [Fact]
    public void Excluded_subtree_roots_are_a_view_and_not_a_way_in()
    {
        var excluded = new ExcludedSubtrees();
        excluded.Add("Secret", new PerimeterVerdict(false, false, ExcludedByAttributes: true));

        var writeThrough = () => ((IDictionary<string, PerimeterVerdict>)excluded.Roots).Clear();

        writeThrough.Should().Throw<NotSupportedException>();
        excluded.Count.Should().Be(1);
        excluded.Roots.Should().ContainKey("Secret");
    }

    /// <summary>
    /// <c>Add</c> UNIONS a second verdict for the same path instead of overwriting it, and until
    /// now nothing held that: putting last-write-wins back left the whole suite green, because no
    /// pipeline reaches a second <c>Add</c> on one path today — the scan enumerates each directory
    /// once and the delta coalesces its records by FRN before classifying.
    ///
    /// <para>"Nothing reaches it today" is the argument for keeping the union cheap, not for
    /// leaving it unguarded. It is the invariant this type exists to keep: overwrite the attribute
    /// cause with the path cause and dropping the segment walks a HIDDEN folder's content back into
    /// the Catalog with no scan, which is the 11h regression through a new door. Asserted in both
    /// orders, because a union that only works one way is a precedence wearing a different name.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Excluded_subtree_causes_sum_when_the_same_path_is_added_twice(bool attributesFirst)
    {
        var attributes = new PerimeterVerdict(false, ExcludedByPath: false, ExcludedByAttributes: true);
        var path = new PerimeterVerdict(false, ExcludedByPath: true, ExcludedByAttributes: false);

        var excluded = new ExcludedSubtrees();
        excluded.Add("Secret", attributesFirst ? attributes : path);
        excluded.Add("Secret", attributesFirst ? path : attributes);

        excluded.Count.Should().Be(1, "it is one directory, however many rules refused it");
        excluded.Roots["Secret"].Should().Be(
            new PerimeterVerdict(false, ExcludedByPath: true, ExcludedByAttributes: true),
            "each cause has to be switchable off by its own owner, so neither may overwrite the other");
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
