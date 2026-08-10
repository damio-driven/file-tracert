using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

public sealed class FileSearchIndexTests
{
    // Named record so we can implement IDisposable cleanly.
    private sealed record TestSetup(SqliteInMemoryContext Harness, FileTracertDbContext Ctx, FileSearchIndex Fts) : IDisposable
    {
        public void Dispose() => Harness.Dispose();
    }

    private static async Task<TestSetup> SetupAsync()
    {
        var harness = new SqliteInMemoryContext();
        var ctx = harness.CreateContext();

        // EnsureCreated builds EF tables but not virtual tables — create FTS5 manually.
        await ctx.Database.ExecuteSqlRawAsync("""
            CREATE VIRTUAL TABLE IF NOT EXISTS FileSearchIndex USING fts5(
                name,
                path,
                tokenize="unicode61 remove_diacritics 2 separators '\._-'"
            );
            """);

        return new TestSetup(harness, ctx, new FileSearchIndex(ctx));
    }

    private static async Task<(int VolumeId, int DirId)> SeedVolumeAndDirAsync(FileTracertDbContext ctx)
    {
        var volume = new Volume
        {
            VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
        };
        ctx.Volumes.Add(volume);
        await ctx.SaveChangesAsync();

        var root = new DirectoryNode
        {
            VolumeId = volume.Id,
            Name = string.Empty,
            MaterializedPath = string.Empty,
            IsMaterialized = true,
        };
        ctx.Directories.Add(root);
        await ctx.SaveChangesAsync();

        return (volume.Id, root.Id);
    }

    private static async Task<FileEntry> AddFileAsync(
        FileTracertDbContext ctx, int volumeId, int dirId, string name, string ext,
        FileCategory cat = FileCategory.Image, DateTime? modifiedUtc = null)
    {
        var f = new FileEntry
        {
            VolumeId = volumeId,
            DirectoryId = dirId,
            Name = name,
            Extension = ext,
            Category = cat,
            SizeBytes = 1024,
            FileCreatedUtc = DateTime.UtcNow,
            FileModifiedUtc = modifiedUtc ?? DateTime.UtcNow,
            IsIncluded = true,
            IsPresent = true,
            LastIndexedUtc = DateTime.UtcNow,
        };
        ctx.Files.Add(f);
        await ctx.SaveChangesAsync();
        return f;
    }

    [Fact]
    public async Task SyncVolume_populates_fts_from_files()
    {
        var setup = await SetupAsync();
        using var harness = setup.Harness;
        var ctx = setup.Ctx;
        var fts = setup.Fts;

        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        var file = await AddFileAsync(ctx, volId, dirId, "vacation.jpg", "jpg");

        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var result = await fts.SearchAsync(
            new FileSearchQuery("vacation", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        result.TotalCount.Should().Be(1);
        result.Items.Should().Contain(file.Id);
    }

    [Fact]
    public async Task SyncVolume_path_includes_directory()
    {
        var setup = await SetupAsync();
        using var harness = setup.Harness;
        var ctx = setup.Ctx;
        var fts = setup.Fts;

        var (volId, rootId) = await SeedVolumeAndDirAsync(ctx);

        var photos = new DirectoryNode
        {
            VolumeId = volId, ParentId = rootId, Name = "Photos",
            MaterializedPath = "Photos", IsMaterialized = true,
        };
        ctx.Directories.Add(photos);
        await ctx.SaveChangesAsync();

        var file = await AddFileAsync(ctx, volId, photos.Id, "city.jpg", "jpg");
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var result = await fts.SearchAsync(
            new FileSearchQuery("Photos", SearchScope.FullPath, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        result.Items.Should().Contain(file.Id);
    }

    [Fact]
    public async Task SyncVolume_name_scope_does_not_match_directory_only()
    {
        var setup = await SetupAsync();
        using var harness = setup.Harness;
        var ctx = setup.Ctx;
        var fts = setup.Fts;

        var (volId, rootId) = await SeedVolumeAndDirAsync(ctx);

        var subDir = new DirectoryNode
        {
            VolumeId = volId, ParentId = rootId, Name = "UniqueFolder",
            MaterializedPath = "UniqueFolder", IsMaterialized = true,
        };
        ctx.Directories.Add(subDir);
        await ctx.SaveChangesAsync();

        var file = await AddFileAsync(ctx, volId, subDir.Id, "report.pdf", "pdf");
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var nameResult = await fts.SearchAsync(
            new FileSearchQuery("UniqueFolder", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        nameResult.TotalCount.Should().Be(0);

        var pathResult = await fts.SearchAsync(
            new FileSearchQuery("UniqueFolder", SearchScope.FullPath, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        pathResult.Items.Should().Contain(file.Id);
    }

    [Fact]
    public async Task Search_accent_insensitive_prefix()
    {
        var setup = await SetupAsync();
        using var harness = setup.Harness;
        var ctx = setup.Ctx;
        var fts = setup.Fts;

        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        var file = await AddFileAsync(ctx, volId, dirId, "città.jpg", "jpg");
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var result = await fts.SearchAsync(
            new FileSearchQuery("citta", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        result.Items.Should().Contain(file.Id);
    }

    [Fact]
    public async Task ClearVolume_removes_fts_entries()
    {
        var setup = await SetupAsync();
        using var harness = setup.Harness;
        var ctx = setup.Ctx;
        var fts = setup.Fts;

        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        await AddFileAsync(ctx, volId, dirId, "removeme.jpg", "jpg");
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var before = await fts.SearchAsync(
            new FileSearchQuery("removeme", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        before.TotalCount.Should().Be(1);

        await fts.ClearVolumeAsync(volId, CancellationToken.None);

        var after = await fts.SearchAsync(
            new FileSearchQuery("removeme", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        after.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task RebuildAsync_populates_all_volumes()
    {
        var setup = await SetupAsync();
        using var harness = setup.Harness;
        var ctx = setup.Ctx;
        var fts = setup.Fts;

        var (volId1, dirId1) = await SeedVolumeAndDirAsync(ctx);
        var (volId2, dirId2) = await SeedVolumeAndDirAsync(ctx);
        await AddFileAsync(ctx, volId1, dirId1, "alpha.jpg", "jpg");
        await AddFileAsync(ctx, volId2, dirId2, "beta.mp4", "mp4");

        await fts.RebuildAsync(CancellationToken.None);

        var alphaResult = await fts.SearchAsync(
            new FileSearchQuery("alpha", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        var betaResult = await fts.SearchAsync(
            new FileSearchQuery("beta", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        alphaResult.TotalCount.Should().Be(1);
        betaResult.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Search_category_filter_narrows_results()
    {
        var setup = await SetupAsync();
        using var harness = setup.Harness;
        var ctx = setup.Ctx;
        var fts = setup.Fts;

        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        await AddFileAsync(ctx, volId, dirId, "photo.jpg", "jpg", FileCategory.Image);
        await AddFileAsync(ctx, volId, dirId, "photo.mp4", "mp4", FileCategory.Video);
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var result = await fts.SearchAsync(
            new FileSearchQuery("photo", SearchScope.Name, FileCategory.Image, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        result.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);
    }

    /// <summary>
    /// The date bounds are compared against a TEXT column, so they must be handed to
    /// SQLite in the provider's storage format. An ISO-8601 round-trip string sorts
    /// wrong against it (' ' 0x20 &lt; 'T' 0x54): midnight-from excluded the whole day
    /// and midnight-to swallowed it (review finding #11).
    /// </summary>
    [Fact]
    public async Task Search_modified_from_includes_files_modified_later_that_day()
    {
        var setup = await SetupAsync();
        using var harness = setup.Harness;
        var ctx = setup.Ctx;
        var fts = setup.Fts;

        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        var afternoon = new DateTime(2026, 7, 3, 14, 20, 29, 912, DateTimeKind.Utc);
        var file = await AddFileAsync(ctx, volId, dirId, "vacation.jpg", "jpg", modifiedUtc: afternoon);
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var midnight = new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc);
        var result = await fts.SearchAsync(
            new FileSearchQuery("vacation", SearchScope.Name, null, null, null, null, midnight, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        result.TotalCount.Should().Be(1);
        result.Items.Should().Contain(file.Id);
    }

    [Fact]
    public async Task Search_modified_to_excludes_files_modified_after_the_bound()
    {
        var setup = await SetupAsync();
        using var harness = setup.Harness;
        var ctx = setup.Ctx;
        var fts = setup.Fts;

        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        var afternoon = new DateTime(2026, 7, 3, 14, 20, 29, 912, DateTimeKind.Utc);
        await AddFileAsync(ctx, volId, dirId, "vacation.jpg", "jpg", modifiedUtc: afternoon);
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var midnight = new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc);
        var excluded = await fts.SearchAsync(
            new FileSearchQuery("vacation", SearchScope.Name, null, null, null, null, null, midnight, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        excluded.TotalCount.Should().Be(0);
        excluded.Items.Should().BeEmpty();

        // …and the natural "up to and including that whole day" bound still finds it.
        var endOfDay = new DateTime(2026, 7, 3, 23, 59, 59, 999, DateTimeKind.Utc);
        var included = await fts.SearchAsync(
            new FileSearchQuery("vacation", SearchScope.Name, null, null, null, null, null, endOfDay, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        included.TotalCount.Should().Be(1);
    }
}
