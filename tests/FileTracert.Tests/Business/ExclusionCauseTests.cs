using FileTracert.Business.Filtering;
using FileTracert.Business.Scanning;
using FileTracert.Business.Setup;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using FileTracert.Data.Search;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// A row does not just record THAT it is excluded, it records WHY — and reconciliation only undoes
/// the causes it can know about (step 11h).
///
/// <para>Everything real: SQLite, <c>FileFilter</c>, <c>ScanService</c>, <c>FilterReconciler</c>,
/// <c>FilterSettingsService</c>, <c>WatchedRootsService</c> and the FTS5 index. Only the disk is
/// faked, because the disk is the one thing a unit test cannot own.</para>
/// </summary>
public sealed class ExclusionCauseTests
{
    private const string Guid = @"\\?\Volume{7B7B7B7B-7B7B-7B7B-7B7B-7B7B7B7B7B7B}\";
    private static readonly DateTime T = new(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);

    private static ProbedVolume Probed() => new(
        Guid, "SER-11H", "Disk", "exFAT", IsRemovable: false,
        MountPoints: [@"X:\"], CapacityBytes: 5000, FreeBytes: 2000, PhysicalDiskId: null);

    private static int Seed(SqliteInMemoryContext harness, List<string> extensions, string rootPath = "")
    {
        using var ctx = harness.CreateContext();

        // EnsureCreated builds the EF tables and not the FTS5 virtual one: these tests assert that
        // the Catalog and the Search screen agree, so the index has to be the real one.
        SqliteFts.Create(ctx);

        ctx.AppSettings.RemoveRange(ctx.AppSettings);
        ctx.AppSettings.Add(new AppSettings
        {
            DefaultExtensionFilter = extensions,
            ExcludedPaths = [],
            ApiToken = "token",
            SpaceMarginPercent = 5,
        });

        var volume = new Volume
        {
            VolumeGuid = Guid, FileSystem = "exFAT", ScanEngine = VolumeScanEngine.Enumeration, IsOnline = true,
        };
        ctx.Volumes.Add(volume);
        ctx.SaveChanges();

        ctx.WatchedRoots.Add(new WatchedRoot { VolumeId = volume.Id, RelativePath = rootPath, IsActive = true });
        ctx.SaveChanges();
        return volume.Id;
    }

    private static async Task ScanAsync(SqliteInMemoryContext harness, int volumeId, params ScanEntry[] entries)
    {
        await using var ctx = harness.CreateContext();
        var writer = new BulkIndexWriter(ctx);
        var sut = new ScanService(ctx,
            new FakeVolumeProbe(Probed()),
            new FakeUsnReader([], 0),
            new FakeDirectoryEnumerator([.. entries]),
            new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
            writer,
            new DirectoryMerger(ctx, writer, NullLogger<DirectoryMerger>.Instance),
            new FileSearchIndex(ctx),
            new FakeNotificationPublisher(),
            new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System),
            NullLogger<ScanService>.Instance);
        await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
    }

    /// <summary>Changes the global type filter exactly the way the Setup screen does.</summary>
    private static Task<ReconcileResultDto> SetAllowedExtensionsAsync(
        SqliteInMemoryContext harness, params string[] extensions) =>
        SetFilterAsync(harness, extensions, []);

    /// <summary>The same screen, both halves of the filter: allowed types and excluded segments.</summary>
    private static async Task<ReconcileResultDto> SetFilterAsync(
        SqliteInMemoryContext harness, string[] extensions, string[] excludedPaths)
    {
        await using var ctx = harness.CreateContext();
        var service = new FilterSettingsService(ctx, new FilterReconciler(ctx, new FileSearchIndex(ctx)));
        return await service.UpdateAsync(
            new FilterSettingsDto(extensions.ToList(), excludedPaths.ToList()), CancellationToken.None);
    }

    private static ScanEntry Dir(string path, string name, FileAttributes extra = FileAttributes.None) =>
        new(path, name, true, 0, T, T, FileAttributes.Directory | extra);

    private static ScanEntry File(string path, string name) =>
        new(path, name, false, 10, T, T, FileAttributes.Normal);

    /// <summary>
    /// THE defect step 11g left open. A file the scan skipped because its folder is Hidden is
    /// <c>IsIncluded = false</c> — the same bit a type-filtered file carries. Widening the type
    /// filter used to re-include it in bulk: the reconciler saw an allowed extension and knew
    /// nothing about the hidden folder, so the content of a folder the user hid reappeared in the
    /// Catalog until the next scan pushed it back out.
    /// </summary>
    [Fact]
    public async Task Widening_the_type_filter_does_not_re_include_what_the_scan_skipped()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg"]);

        ScanEntry[] Entries(FileAttributes secret) =>
        [
            Dir("Photos", "Photos"),
            File(@"Photos\holiday.jpg", "holiday.jpg"),
            Dir("Secret", "Secret", secret),
            File(@"Secret\private.jpg", "private.jpg"),
        ];

        await ScanAsync(harness, volumeId, Entries(FileAttributes.None));
        await ScanAsync(harness, volumeId, Entries(FileAttributes.Hidden));

        await using (var arrange = harness.CreateContext())
        {
            (await arrange.Files.SingleAsync(f => f.Name == "private.jpg")).IsIncluded
                .Should().BeFalse("arrange: the scan skipped it because its folder is hidden");
        }

        // The user widens the type filter. Nothing about the hidden folder changed.
        await SetAllowedExtensionsAsync(harness, "jpg", "png");

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "private.jpg")).IsIncluded
            .Should().BeFalse("a wider type filter says nothing about a folder the scan was told to skip");
        (await read.Files.SingleAsync(f => f.Name == "holiday.jpg")).IsIncluded
            .Should().BeTrue("the file inside the perimeter is unaffected");
    }

    /// <summary>
    /// The other half of the same rule: what the TYPE filter excluded must come back when the type
    /// filter widens, with no scan. That is what reconciliation is for (§4), and it must survive
    /// the fix above.
    /// </summary>
    [Fact]
    public async Task Widening_the_type_filter_re_includes_what_the_type_filter_excluded()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg", "png"]);

        await ScanAsync(harness, volumeId,
            Dir("Photos", "Photos"),
            File(@"Photos\a.jpg", "a.jpg"),
            File(@"Photos\b.png", "b.png"));

        await SetAllowedExtensionsAsync(harness, "jpg");

        await using (var narrowed = harness.CreateContext())
        {
            (await narrowed.Files.SingleAsync(f => f.Name == "b.png")).IsIncluded
                .Should().BeFalse("arrange: the type filter rejects it now");
        }

        var result = await SetAllowedExtensionsAsync(harness, "jpg", "png");

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "b.png")).IsIncluded
            .Should().BeTrue("the type filter is the cause, and it is the one reconciliation owns");
        result.IncludedCount.Should().Be(2);
    }

    /// <summary>
    /// A root switched off and on again re-includes without a scan (§4, step 11g) — but it must
    /// not resurrect the rows the scan itself skipped while the root was on.
    /// </summary>
    [Fact]
    public async Task A_root_switched_back_on_leaves_what_the_scan_skipped_excluded()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg"], rootPath: "Photos");

        ScanEntry[] Entries(FileAttributes secret) =>
        [
            Dir("Photos", "Photos"),
            File(@"Photos\holiday.jpg", "holiday.jpg"),
            Dir(@"Photos\Secret", "Secret", secret),
            File(@"Photos\Secret\private.jpg", "private.jpg"),
        ];

        await ScanAsync(harness, volumeId, Entries(FileAttributes.None));
        await ScanAsync(harness, volumeId, Entries(FileAttributes.Hidden));

        var rootId = await RootIdAsync(harness, volumeId);
        await ToggleRootAsync(harness, rootId, isActive: false);
        await ToggleRootAsync(harness, rootId, isActive: true);

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "holiday.jpg")).IsIncluded
            .Should().BeTrue("the perimeter is back, and its rows with it — no scan needed");
        (await read.Files.SingleAsync(f => f.Name == "private.jpg")).IsIncluded
            .Should().BeFalse("the hidden folder is still hidden; only a scan can say otherwise");
    }

    /// <summary>
    /// Second gap step 11g documented: reconciliation moves <c>IsIncluded</c> and never touched
    /// FTS5. The Catalog reads the flag, so it recovers on its own; Search reads the INDEX, and the
    /// scan closure had already pruned the entry away. Re-widening the filter therefore produced a
    /// file you can navigate to and cannot find — until some later scan happened to pass.
    /// </summary>
    [Fact]
    public async Task Reconciliation_puts_back_the_search_entries_a_scan_pruned()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg", "png"]);

        ScanEntry[] entries =
        [
            Dir("Photos", "Photos"),
            File(@"Photos\alpha.jpg", "alpha.jpg"),
            File(@"Photos\beta.png", "beta.png"),
        ];

        await ScanAsync(harness, volumeId, entries);
        (await SearchAsync(harness, "beta")).Should().HaveCount(1, "arrange: it starts findable");

        // Narrow, then let a scan close over it: that is what drops the entry from the index.
        await SetAllowedExtensionsAsync(harness, "jpg");
        await ScanAsync(harness, volumeId, entries);
        (await SearchAsync(harness, "beta")).Should()
            .BeEmpty("arrange: an excluded file is not a search hit");

        // Widen again — and this time NO scan follows. The two screens must agree anyway.
        await SetAllowedExtensionsAsync(harness, "jpg", "png");

        await using (var catalog = harness.CreateContext())
        {
            (await catalog.Files.SingleAsync(f => f.Name == "beta.png")).IsIncluded
                .Should().BeTrue("arrange: the Catalog has it back");
        }

        (await SearchAsync(harness, "beta")).Should()
            .HaveCount(1, "back in the Catalog means back in Search, without waiting for a scan");
        (await SearchAsync(harness, "alpha")).Should()
            .HaveCount(1, "and the untouched row is neither lost nor duplicated");
    }

    /// <summary>The same, for the perimeter half: a root switched off, scanned over, then back on.</summary>
    [Fact]
    public async Task Switching_a_root_back_on_puts_back_the_search_entries_a_scan_pruned()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg"], rootPath: "Photos");

        // A second root keeps the scan alive once the first is switched off — with no active root
        // at all the scan has nothing to do and returns early.
        await using (var ctx = harness.CreateContext())
        {
            ctx.WatchedRoots.Add(new WatchedRoot { VolumeId = volumeId, RelativePath = "Other", IsActive = true });
            await ctx.SaveChangesAsync();
        }

        ScanEntry[] entries =
        [
            Dir("Photos", "Photos"),
            File(@"Photos\alpha.jpg", "alpha.jpg"),
            Dir("Other", "Other"),
            File(@"Other\gamma.jpg", "gamma.jpg"),
        ];

        await ScanAsync(harness, volumeId, entries);
        (await SearchAsync(harness, "alpha")).Should().HaveCount(1, "arrange: it starts findable");

        var rootId = await RootIdAsync(harness, volumeId, "Photos");
        await ToggleRootAsync(harness, rootId, isActive: false);
        await ScanAsync(harness, volumeId, entries);
        (await SearchAsync(harness, "alpha")).Should().BeEmpty("arrange: outside the perimeter, outside Search");

        await ToggleRootAsync(harness, rootId, isActive: true);
        (await SearchAsync(harness, "alpha")).Should().HaveCount(1, "and back in, with no scan in between");
    }

    /// <summary>
    /// A <c>.txt</c> inside a hidden folder is excluded TWICE over. Undoing one cause must not
    /// undo the other — which is the whole reason the causes are flags and not a single value.
    /// </summary>
    [Fact]
    public async Task A_row_excluded_by_two_causes_needs_both_undone()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg"]);

        ScanEntry[] Entries(FileAttributes secret) =>
        [
            Dir("Secret", "Secret", secret),
            File(@"Secret\notes.txt", "notes.txt"),
            File(@"Secret\pic.jpg", "pic.jpg"),
        ];

        // Indexed while everything is allowed and visible…
        await SetAllowedExtensionsAsync(harness);
        await ScanAsync(harness, volumeId, Entries(FileAttributes.None));

        // …then the type filter narrows AND the folder is hidden.
        await SetAllowedExtensionsAsync(harness, "jpg");
        await ScanAsync(harness, volumeId, Entries(FileAttributes.Hidden));

        // Undo only the type cause.
        await SetAllowedExtensionsAsync(harness);

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "notes.txt")).IsIncluded
            .Should().BeFalse("the folder is still hidden: one cause undone leaves the other standing");
        (await read.Files.SingleAsync(f => f.Name == "pic.jpg")).IsIncluded
            .Should().BeFalse("its only cause was the hidden folder, and that one is not reconcilable");
    }

    // ── step 16: the path half of the perimeter is a settings fact ────────────────────────────

    /// <summary>
    /// THE defect of step 16, and the worst shape a defect can take: a decision the user made,
    /// acknowledged by the screen, and applied to nothing. Adding a segment to
    /// <c>ExcludedPaths</c> left every row already in the catalog <c>IsIncluded = 1</c> — still
    /// navigable, still findable in Search — because the only column that could have recorded it
    /// was <c>ExcludedByScan</c>, which reconciliation may never write.
    /// </summary>
    [Fact]
    public async Task Excluding_a_path_segment_excludes_the_rows_already_in_the_catalog()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg"]);

        await ScanAsync(harness, volumeId,
            Dir("AppData", "AppData"),
            Dir(@"AppData\Cache", "Cache"),
            File(@"AppData\Cache\thumb.jpg", "thumb.jpg"),
            Dir("Photos", "Photos"),
            File(@"Photos\holiday.jpg", "holiday.jpg"));

        (await SearchAsync(harness, "thumb")).Should().HaveCount(1, "arrange: it starts findable");

        var result = await SetFilterAsync(harness, ["jpg"], ["AppData"]);

        await using var read = harness.CreateContext();
        var thumb = await read.Files.SingleAsync(f => f.Name == "thumb.jpg");
        thumb.IsIncluded.Should().BeFalse("the user excluded the segment its path goes through");
        thumb.ExcludedByPath.Should().BeTrue("and the row records WHICH cause, so it can be undone");
        thumb.ExcludedByScan.Should().BeFalse("nothing here is a fact about the disk");
        thumb.IsPresent.Should().BeTrue("an exclusion is not an absence (§6) — the file is still there");

        (await SearchAsync(harness, "thumb")).Should()
            .BeEmpty("Catalog and Search have to agree without waiting for a scan");
        (await read.Files.SingleAsync(f => f.Name == "holiday.jpg")).IsIncluded
            .Should().BeTrue("a sibling outside the segment is untouched");

        // The note the Setup screen shows is these three fields. It has to read as
        // "index realigned: 1 file included · 1 excluded", with no scan asked for.
        result.IncludedCount.Should().Be(1, "the count must not call an excluded row included");
        result.ExcludedCount.Should().Be(1);
        result.NeedsScan.Should().BeFalse(
            "a narrowing is applied in full right here; there is nothing left for a scan to do");
    }

    /// <summary>
    /// The other direction, and the point of a settings-borne cause: dropping the segment puts the
    /// rows back with no scan (§4). It is what <c>ExcludedByScan</c> could never do.
    /// </summary>
    [Fact]
    public async Task Dropping_the_segment_puts_the_rows_back_without_a_scan()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg"]);

        await ScanAsync(harness, volumeId,
            Dir("AppData", "AppData"),
            File(@"AppData\thumb.jpg", "thumb.jpg"));

        await SetFilterAsync(harness, ["jpg"], ["AppData"]);
        await using (var arrange = harness.CreateContext())
        {
            (await arrange.Files.SingleAsync(f => f.Name == "thumb.jpg")).IsIncluded
                .Should().BeFalse("arrange: the segment excludes it");
        }

        await SetFilterAsync(harness, ["jpg"], []);

        await using var read = harness.CreateContext();
        var thumb = await read.Files.SingleAsync(f => f.Name == "thumb.jpg");
        thumb.IsIncluded.Should().BeTrue("the cause was a setting, and the setting is gone");
        thumb.ExcludedByPath.Should().BeFalse();
        (await SearchAsync(harness, "thumb")).Should()
            .HaveCount(1, "back in the Catalog means back in Search, with no scan in between");
    }

    /// <summary>
    /// A segment matches whole segments only, exactly as <c>FileFilter.IsPathExcluded</c> does when
    /// a scan asks — and the file NAME is one of the segments it splits, so the two have to agree
    /// there too. If they disagree, a scan and a reconciliation give the catalog different answers
    /// about the same file.
    /// </summary>
    [Fact]
    public async Task The_segment_match_is_the_one_the_scan_uses()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, []);

        await ScanAsync(harness, volumeId,
            Dir("Temp", "Temp"),
            File(@"Temp\inside.jpg", "inside.jpg"),
            Dir("Temporary", "Temporary"),
            File(@"Temporary\sibling.jpg", "sibling.jpg"),
            File("Temp.jpg", "Temp.jpg"),
            File("Temp", "Temp"));

        await SetFilterAsync(harness, [], ["temp"]);

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "inside.jpg")).IsIncluded
            .Should().BeFalse("its directory IS the segment");
        (await read.Files.SingleAsync(f => f.Name == "sibling.jpg")).IsIncluded
            .Should().BeTrue("segment-aware: Temp is not a prefix of Temporary");
        (await read.Files.SingleAsync(f => f.Name == "Temp.jpg")).IsIncluded
            .Should().BeTrue("a file that merely starts with the segment is not under it");
        (await read.Files.Where(f => f.Name == "Temp").SingleAsync()).IsIncluded
            .Should().BeFalse("the file NAME is a segment too — that is what IsPathExcluded splits");
    }

    /// <summary>
    /// A LIKE metacharacter sitting in a configured segment is a character of a folder NAME, not a
    /// wildcard the user asked for. Both neighbours here are chosen so that an unescaped pattern
    /// would swallow them: <c>%\100%\%</c> matches <c>\1000\…\</c>, and <c>%\Te_p\%</c> matches
    /// <c>\Temp\…\</c>. An exclusion that quietly takes more than it was given is worse than one
    /// that takes nothing.
    /// </summary>
    [Fact]
    public async Task A_wildcard_in_a_segment_is_matched_literally()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, []);

        await ScanAsync(harness, volumeId,
            Dir("100%", "100%"),
            File(@"100%\literal.jpg", "literal.jpg"),
            Dir("1000", "1000"),
            File(@"1000\neighbour.jpg", "neighbour.jpg"),
            Dir("Te_p", "Te_p"),
            File(@"Te_p\underscore.jpg", "underscore.jpg"),
            Dir("Temp", "Temp"),
            File(@"Temp\anychar.jpg", "anychar.jpg"));

        await SetFilterAsync(harness, [], ["100%", "Te_p"]);

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "literal.jpg")).IsIncluded
            .Should().BeFalse("the folder IS the configured segment");
        (await read.Files.SingleAsync(f => f.Name == "underscore.jpg")).IsIncluded
            .Should().BeFalse("so is this one");
        (await read.Files.SingleAsync(f => f.Name == "neighbour.jpg")).IsIncluded
            .Should().BeTrue("the % is a character, not 'anything'");
        (await read.Files.SingleAsync(f => f.Name == "anychar.jpg")).IsIncluded
            .Should().BeTrue("the _ is a character, not 'any single character'");
    }

    /// <summary>
    /// The causes SUM, which is the reason they are four flags and not one value: a <c>.tmp</c>
    /// under an excluded segment is out twice over, and undoing one leaves the other standing.
    /// </summary>
    [Fact]
    public async Task A_row_excluded_by_type_and_by_path_needs_both_undone()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, []);

        await ScanAsync(harness, volumeId,
            Dir("AppData", "AppData"),
            File(@"AppData\notes.tmp", "notes.tmp"),
            File(@"AppData\pic.jpg", "pic.jpg"));

        await SetFilterAsync(harness, ["jpg"], ["AppData"]);

        await using (var narrowed = harness.CreateContext())
        {
            var tmp = await narrowed.Files.SingleAsync(f => f.Name == "notes.tmp");
            tmp.ExcludedByType.Should().BeTrue();
            tmp.ExcludedByPath.Should().BeTrue("both causes are recorded, because both are true");
        }

        // Undo only the type half.
        await SetFilterAsync(harness, [], ["AppData"]);

        await using var read = harness.CreateContext();
        var notes = await read.Files.SingleAsync(f => f.Name == "notes.tmp");
        notes.IsIncluded.Should().BeFalse("the segment is still excluded: one cause undone, one standing");
        notes.ExcludedByType.Should().BeFalse();
        notes.ExcludedByPath.Should().BeTrue();
        (await read.Files.SingleAsync(f => f.Name == "pic.jpg")).IsIncluded
            .Should().BeFalse("its only cause was the segment, and that one has not moved");
    }

    /// <summary>
    /// A folder that is hidden AND on the excluded list fails both perimeter rules, and the row
    /// records the PATH one. The consequence is what matters: the cause written there is the one
    /// reconciliation can re-decide, so the row is not pinned out for the life of the catalog by a
    /// verdict no setting can reach. The other order costs nothing less than that — and erring this
    /// way costs one scan, which re-stamps the row with whichever rule still holds.
    /// </summary>
    [Fact]
    public async Task A_folder_that_fails_both_rules_records_the_one_that_can_be_undone()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg"]);

        ScanEntry[] Entries(FileAttributes secret) =>
        [
            Dir("Secret", "Secret", secret),
            File(@"Secret\both.jpg", "both.jpg"),
        ];

        // Indexed while the folder is plain and no segment is excluded…
        await ScanAsync(harness, volumeId, Entries(FileAttributes.None));

        // …then BOTH rules turn against it: the segment is excluded, and the folder goes hidden.
        // The scan is what re-decides the row, and which cause it stamps is the question.
        await SetFilterAsync(harness, ["jpg"], ["Secret"]);
        await ScanAsync(harness, volumeId, Entries(FileAttributes.Hidden));

        await using (var scanned = harness.CreateContext())
        {
            var both = await scanned.Files.SingleAsync(f => f.Name == "both.jpg");
            both.IsIncluded.Should().BeFalse();
            both.ExcludedByPath.Should().BeTrue("the segment is the cause a setting can retract");
            both.ExcludedByScan.Should().BeFalse(
                "recording the attribute cause instead would pin the row out for ever");
        }

        // And the proof that it is not pinned: the setting that put it there can take it back.
        await SetFilterAsync(harness, ["jpg"], []);

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "both.jpg")).IsIncluded.Should().BeTrue(
            "the row carried a settings-borne cause, and the setting is gone — a later scan is what " +
            "re-applies the attribute rule, which is the accepted price of this precedence");
    }

    /// <summary>
    /// The regression step 11h exists to prevent, checked against the new lever: a file inside a
    /// HIDDEN folder must stay out however the segments are edited. Nothing in Setup knows whether
    /// that folder is still hidden.
    /// </summary>
    [Fact]
    public async Task A_file_in_a_hidden_folder_survives_every_change_to_the_segments()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, ["jpg"]);

        ScanEntry[] Entries(FileAttributes secret) =>
        [
            Dir("Secret", "Secret", secret),
            File(@"Secret\private.jpg", "private.jpg"),
        ];

        await ScanAsync(harness, volumeId, Entries(FileAttributes.None));
        await ScanAsync(harness, volumeId, Entries(FileAttributes.Hidden));

        await SetFilterAsync(harness, ["jpg"], ["Secret"]);
        await SetFilterAsync(harness, ["jpg"], []);

        await using var read = harness.CreateContext();
        var priv = await read.Files.SingleAsync(f => f.Name == "private.jpg");
        priv.IsIncluded.Should().BeFalse("the folder is still hidden; only a scan can say otherwise");
        priv.ExcludedByScan.Should().BeTrue("the attribute cause is the one reconciliation never touches");
        priv.IsPresent.Should().BeTrue();
    }

    /// <summary>
    /// Re-widening a filter on a big volume touches hundreds of thousands of rows, so the pass has
    /// to stay set-based — flags AND search index alike. Measured in statements, not milliseconds
    /// (the same unit step 11g used, and the only one a test can assert): ten times the files, the
    /// same statements.
    ///
    /// <para>What it does NOT prove: the search-index half is chunked by DIRECTORY (500 ids per
    /// pair of statements), so a volume with tens of thousands of folders does grow — by
    /// directories, never by files, which is the trade
    /// <see cref="IFileSearchIndex.SyncDirectoriesAsync"/> exists to make.</para>
    /// </summary>
    [Fact]
    public async Task Reconciliation_costs_the_same_whatever_the_number_of_rows()
    {
        var few = await ReconcileCostAsync(files: 50);
        var many = await ReconcileCostAsync(files: 500);

        many.Should().Be(few, "reconciliation is set-based: ten times the rows, the same statements");
        few.Should().Be(6,
            "three UPDATEs for the causes, the SELECT that names the subtree's directories, and " +
            "the FTS DELETE + INSERT pair that follows them");
    }

    /// <summary>
    /// The path half is decided in SQL, in ONE statement per group and with the OR of every
    /// segment inside it — not one statement per segment. It runs inside the Setup transaction,
    /// which holds SQLite's only write lock, and the segments are a handful by nature
    /// (<c>Windows</c>, <c>Program Files</c>, <c>$Recycle.Bin</c>, <c>AppData</c>).
    /// </summary>
    [Fact]
    public async Task Deciding_the_path_half_costs_a_fixed_number_of_statements()
    {
        var few = await ReconcileCostAsync(files: 50, "AppData", "Windows", "$Recycle.Bin");
        var many = await ReconcileCostAsync(files: 500, "AppData", "Windows", "$Recycle.Bin");

        many.Should().Be(few, "still set-based with the path predicate in play");
        few.Should().Be(8,
            "the three-way split becomes five — path-excluded, included, and the rest, times the " +
            "type verdict — plus the same SELECT and FTS pair");

        var oneSegment = await ReconcileCostAsync(files: 50, "AppData");
        oneSegment.Should().Be(few, "the segments are ORed inside one statement, not one each");
    }

    private static async Task<int> ReconcileCostAsync(int files, params string[] excludedSegments)
    {
        var connection = new CountingSqliteConnection("Data Source=:memory:");
        using var harness = new SqliteInMemoryContext(connection: connection);
        var volumeId = Seed(harness, ["jpg"]);

        ScanEntry[] entries =
        [
            Dir("Photos", "Photos"),
            .. Enumerable.Range(0, files).Select(i => File($@"Photos\f{i:D4}.jpg", $"f{i:D4}.jpg")),
        ];
        await ScanAsync(harness, volumeId, entries);

        await using var ctx = harness.CreateContext();
        var root = await ctx.WatchedRoots.FirstAsync(r => r.VolumeId == volumeId);
        var reconciler = new FilterReconciler(ctx, new FileSearchIndex(ctx));

        // Counted from here: one reconciliation of one root, nothing else.
        connection.Reset();
        await reconciler.ReconcileRootAsync(
            root,
            new EffectiveFilter(new HashSet<string> { "jpg", "png" }, excludedSegments),
            CancellationToken.None);
        return connection.Statements;
    }

    private static Task<int> RootIdAsync(SqliteInMemoryContext harness, int volumeId, string? relativePath = null)
    {
        using var ctx = harness.CreateContext();
        return ctx.WatchedRoots
            .Where(r => r.VolumeId == volumeId && (relativePath == null || r.RelativePath == relativePath))
            .Select(r => r.Id)
            .FirstAsync();
    }

    private static async Task ToggleRootAsync(SqliteInMemoryContext harness, int rootId, bool isActive)
    {
        await using var ctx = harness.CreateContext();
        var service = new WatchedRootsService(ctx, new FilterReconciler(ctx, new FileSearchIndex(ctx)));
        await service.UpdateAsync(rootId, new UpdateWatchedRootRequest(isActive, null), CancellationToken.None);
    }

    private static async Task<IReadOnlyList<int>> SearchAsync(SqliteInMemoryContext harness, string text)
    {
        await using var ctx = harness.CreateContext();
        var result = await new FileSearchIndex(ctx).SearchAsync(
            new FileSearchQuery(
                Text: text, Scope: SearchScope.Name, Category: null, Extensions: null,
                SizeBytesMin: null, SizeBytesMax: null, ModifiedFrom: null, ModifiedTo: null,
                VolumeId: null, OnlineOnly: false, Sort: SearchSort.Relevance, Desc: false,
                Skip: 0, Take: 50),
            CancellationToken.None);
        return result.Items;
    }
}
