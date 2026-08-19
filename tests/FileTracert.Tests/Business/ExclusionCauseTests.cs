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
    private static async Task<ReconcileResultDto> SetAllowedExtensionsAsync(
        SqliteInMemoryContext harness, params string[] extensions)
    {
        await using var ctx = harness.CreateContext();
        var service = new FilterSettingsService(ctx, new FilterReconciler(ctx));
        return await service.UpdateAsync(
            new FilterSettingsDto(extensions.ToList(), []), CancellationToken.None);
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

    /// <summary>
    /// Re-widening a filter on a big volume touches hundreds of thousands of rows, so the pass has
    /// to stay set-based — flags AND search index alike. Measured in statements, not milliseconds
    /// (the same unit step 11g used, and the only one a test can assert): ten times the files, the
    /// same statements.
    ///
    /// </summary>
    [Fact]
    public async Task Reconciliation_costs_the_same_whatever_the_number_of_rows()
    {
        var few = await ReconcileCostAsync(files: 50);
        var many = await ReconcileCostAsync(files: 500);

        many.Should().Be(few, "reconciliation is set-based: ten times the rows, the same statements");
        few.Should().Be(3, "one UPDATE per partition of the allow-list, and nothing else");
    }

    private static async Task<int> ReconcileCostAsync(int files)
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
        var reconciler = new FilterReconciler(ctx);

        // Counted from here: one reconciliation of one root, nothing else.
        connection.Reset();
        await reconciler.ReconcileRootAsync(
            root, new EffectiveFilter(new HashSet<string> { "jpg", "png" }, []), CancellationToken.None);
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
        var service = new WatchedRootsService(ctx, new FilterReconciler(ctx));
        await service.UpdateAsync(rootId, new UpdateWatchedRootRequest(isActive, null), CancellationToken.None);
    }
}
