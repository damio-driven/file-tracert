using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Business;

/// <summary>
/// C19: a rename used to write the new <c>Name</c> and nothing else, leaving <c>Extension</c>
/// and <c>Category</c> frozen on the OLD name until the next full re-scan. Everything that
/// filters on those two — the search facets, the filter reconciler — worked on dead values:
/// <c>foto.jpg</c> renamed to <c>foto.txt</c> stayed an Image with extension <c>jpg</c>.
///
/// Asserted against the real <see cref="FileSearchIndex"/> (a real FTS5 table on real SQLite)
/// rather than only against the entity, because "the search finds it in the new category" is
/// the part the user actually experiences.
/// </summary>
public sealed class RenameReclassificationTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness = new();

    public RenameReclassificationTests()
    {
        using var setup = _harness.CreateContext();
        setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        // EnsureCreated builds EF tables but not virtual tables — create FTS5 manually.
        SqliteFts.Create(setup);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Rename_recomputes_extension_and_category_and_the_search_follows()
    {
        Seed(newName: "foto.txt");

        await RunRenameAsync();

        await using var probe = _harness.CreateContext();
        var file = await probe.Files.SingleAsync();
        file.Name.Should().Be("foto.txt");
        file.Extension.Should().Be("txt");
        file.Category.Should().Be(FileCategory.Document);
        file.IsIncluded.Should().BeTrue();

        var fts = new FileSearchIndex(probe);
        var asDocument = await fts.SearchAsync(Query("foto", FileCategory.Document), CancellationToken.None);
        asDocument.Items.Should().ContainSingle().Which.Should().Be(file.Id);

        var asImage = await fts.SearchAsync(Query("foto", FileCategory.Image), CancellationToken.None);
        asImage.Items.Should().BeEmpty("the file is no longer an image");
    }

    /// <summary>
    /// §4 reconciliation: a new name outside the allow-list flips <c>IsIncluded</c> to false —
    /// never a delete — and the file leaves the search index with it.
    /// </summary>
    [Fact]
    public async Task Rename_out_of_the_allow_list_excludes_the_file_instead_of_deleting_it()
    {
        Seed(newName: "foto.exe", allowedExtensions: ["jpg", "txt"]);

        await RunRenameAsync();

        await using var probe = _harness.CreateContext();
        var file = await probe.Files.SingleAsync();
        file.IsIncluded.Should().BeFalse();
        file.Extension.Should().Be("exe");

        var fts = new FileSearchIndex(probe);
        var hits = await fts.SearchAsync(Query("foto", Category: null), CancellationToken.None);
        hits.Items.Should().BeEmpty("an excluded file must stop being a search hit");
    }

    /// <summary>A rename back into the allow-list re-includes it — the flag is a filter, not a tombstone.</summary>
    [Fact]
    public async Task Rename_back_into_the_allow_list_re_includes_the_file()
    {
        Seed(newName: "foto.txt", allowedExtensions: ["jpg", "txt"], startsIncluded: false);

        await RunRenameAsync();

        await using var probe = _harness.CreateContext();
        (await probe.Files.SingleAsync()).IsIncluded.Should().BeTrue();

        var fts = new FileSearchIndex(probe);
        var hits = await fts.SearchAsync(Query("foto", Category: null), CancellationToken.None);
        hits.Items.Should().ContainSingle();
    }

    /// <summary>
    /// Step 11h. The rename knows the file's own name, attributes and path — not that a FOLDER
    /// above it is Hidden. So it may not re-include a row the SCAN excluded: it does not have the
    /// information, and saying yes anyway breaks the invariant that keeps the next scan's absence
    /// pass off this row. That pass would then stamp <c>IsPresent = 0</c> on a file sitting on the
    /// disk, which is the exact shape of the bug step 11g existed to remove.
    /// </summary>
    [Fact]
    public async Task Rename_does_not_re_include_a_row_the_scan_excluded()
    {
        Seed(newName: "foto.txt", allowedExtensions: ["jpg", "txt"],
             startsIncluded: false, excludedByScan: true);

        await RunRenameAsync();

        await using var probe = _harness.CreateContext();
        var file = await probe.Files.SingleAsync();
        file.Extension.Should().Be("txt", "the reclassification still happens");
        file.ExcludedByScan.Should().BeTrue("only a scan can retract what a scan decided");
        file.IsIncluded.Should().BeFalse(
            "the allow-list is not the only gate, and the rename cannot see the folder above it");

        var hits = await new FileSearchIndex(probe).SearchAsync(
            Query("foto", Category: null), CancellationToken.None);
        hits.Items.Should().BeEmpty("an excluded row is not a search hit whatever excluded it");
    }

    /// <summary>The invariant, stated once: an included row carries no cause at all.</summary>
    [Fact]
    public async Task Rename_back_into_the_allow_list_clears_the_type_cause()
    {
        Seed(newName: "foto.txt", allowedExtensions: ["jpg", "txt"], startsIncluded: false);

        await RunRenameAsync();

        await using var probe = _harness.CreateContext();
        var file = await probe.Files.SingleAsync();
        file.IsIncluded.Should().BeTrue();
        (file.ExcludedByType || file.ExcludedByRoot || file.ExcludedByScan).Should().BeFalse();
    }

    /// <summary>…and the other direction writes the cause instead of an anonymous flag.</summary>
    [Fact]
    public async Task Rename_out_of_the_allow_list_records_the_type_as_the_cause()
    {
        Seed(newName: "foto.exe", allowedExtensions: ["jpg", "txt"]);

        await RunRenameAsync();

        await using var probe = _harness.CreateContext();
        var file = await probe.Files.SingleAsync();
        file.IsIncluded.Should().BeFalse();
        file.ExcludedByType.Should().BeTrue("the allow-list is what rejected it");
        file.ExcludedByScan.Should().BeFalse("the scan said nothing about this row");
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    private async Task RunRenameAsync()
    {
        await using var db = _harness.CreateContext();
        var job = await db.OperationJobs.Include(j => j.Items).SingleAsync();
        var updater = TestProjection.Index(db, new FileSearchIndex(db));
        await updater.UpdateAfterCompletionAsync(job, CancellationToken.None);
    }

    private void Seed(
        string newName, string[]? allowedExtensions = null, bool startsIncluded = true,
        bool excludedByScan = false)
    {
        using var db = _harness.CreateContext();

        db.AppSettings.Add(new AppSettings
        {
            Id = 1,
            ApiToken = "test",
            DefaultExtensionFilter = [.. allowedExtensions ?? []],
        });

        db.Volumes.Add(new Volume
        {
            Id = 1, VolumeGuid = @"\\?\Volume{s}\", FileSystem = "NTFS", IsOnline = true,
        });
        db.WatchedRoots.Add(new WatchedRoot { Id = 1, VolumeId = 1, RelativePath = "Media", IsActive = true });
        db.Directories.Add(new DirectoryNode
        {
            Id = 10, VolumeId = 1, Name = "Media", MaterializedPath = "Media", IsMaterialized = true,
        });
        db.Files.Add(new FileEntry
        {
            Id = 1, VolumeId = 1, DirectoryId = 10, Name = "foto.jpg", Extension = "jpg",
            Category = FileCategory.Image, SizeBytes = 10, IsPresent = true, IsIncluded = startsIncluded,
            ExcludedByScan = excludedByScan,
            FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
        });

        var job = new OperationJob
        {
            Type = JobType.RenameFile, State = JobState.Completed, IsIntraVolume = true,
            SourceVolumeId = 1, TargetVolumeId = 1, TargetRelativePath = @"Media\" + newName,
            SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        };
        job.Items.Add(new OperationJobItem
        {
            FileId = 1,
            SourceRelativePath = @"Media\foto.jpg",
            TargetRelativePath = @"Media\" + newName,
            SizeBytes = 10, State = JobItemState.Done,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        });
        db.OperationJobs.Add(job);
        db.SaveChanges();

        // The index as the scan would have left it before the rename.
        if (startsIncluded)
        {
            new FileSearchIndex(db).UpsertAsync(1, "foto.jpg", @"Media\foto.jpg", CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    private static FileSearchQuery Query(string text, FileCategory? Category) => new(
        text, SearchScope.Name, Category, Extensions: null,
        SizeBytesMin: null, SizeBytesMax: null, ModifiedFrom: null, ModifiedTo: null,
        VolumeId: null, OnlineOnly: false, SearchSort.Name, Desc: false, Skip: 0, Take: 50);
}
