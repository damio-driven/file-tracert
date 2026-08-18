using FileTracert.Business.Scanning;
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
/// The search index against a merging scan. Rebuilding the whole volume's FTS entries at the
/// end of every scan was acceptable while the scan replaced the volume; now that it only
/// touches what changed, the index is synced per batch — and the risk moves from "stale" to
/// "duplicated", so that is what these tests pin down.
/// </summary>
public sealed class ScanFtsSyncTests
{
    private const string Guid = @"\\?\Volume{77777777-7777-7777-7777-777777777777}\";
    private static readonly DateTime T = new(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_re_scan_leaves_one_search_hit_per_file_and_drops_what_vanished()
    {
        using var harness = new SqliteInMemoryContext();
        await CreateFtsTableAsync(harness);
        var volumeId = await SeedAsync(harness);

        var both = new List<ScanEntry>
        {
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
            new(@"A\alpha.dat", "alpha.dat", false, 1, T, T, FileAttributes.Normal),
            new(@"A\beta.dat", "beta.dat", false, 2, T, T, FileAttributes.Normal),
        };

        await ScanAsync(harness, volumeId, both);
        (await SearchAsync(harness, "alpha")).Should().HaveCount(1);

        // Same disk content: the merge updates the rows, and the index must not gain a
        // second entry for each of them.
        await ScanAsync(harness, volumeId, both);
        var alphaHits = await SearchAsync(harness, "alpha");
        alphaHits.Should().HaveCount(1);

        // beta.dat is gone from disk: the absent pass must take it out of the index too,
        // even though the row itself stays (no hard-delete).
        await ScanAsync(harness, volumeId,
        [
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
            new(@"A\alpha.dat", "alpha.dat", false, 1, T, T, FileAttributes.Normal),
        ]);

        (await SearchAsync(harness, "beta")).Should().BeEmpty();
        (await SearchAsync(harness, "alpha")).Should().Equal(alphaHits);

        await using var read = harness.CreateContext();
        (await read.Files.CountAsync()).Should().Be(2); // the vanished row is kept, just absent
    }

    [Fact]
    public async Task A_file_added_between_scans_becomes_searchable()
    {
        using var harness = new SqliteInMemoryContext();
        await CreateFtsTableAsync(harness);
        var volumeId = await SeedAsync(harness);

        await ScanAsync(harness, volumeId,
        [
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
            new(@"A\alpha.dat", "alpha.dat", false, 1, T, T, FileAttributes.Normal),
        ]);

        await ScanAsync(harness, volumeId,
        [
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
            new(@"A\alpha.dat", "alpha.dat", false, 1, T, T, FileAttributes.Normal),
            new(@"A\gamma.dat", "gamma.dat", false, 3, T, T, FileAttributes.Normal),
        ], batchSize: 1);

        (await SearchAsync(harness, "gamma")).Should().HaveCount(1);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task CreateFtsTableAsync(SqliteInMemoryContext harness)
    {
        // EnsureCreated builds the EF tables but not virtual ones (FTS5 comes from a raw-SQL
        // migration), so the test creates it exactly as the migration does.
        await using var ctx = harness.CreateContext();
        await ctx.Database.ExecuteSqlRawAsync("""
            CREATE VIRTUAL TABLE IF NOT EXISTS FileSearchIndex USING fts5(
                name,
                path,
                tokenize="unicode61 remove_diacritics 2 separators '\._-'"
            );
            """);
    }

    private static async Task<int> SeedAsync(SqliteInMemoryContext harness)
    {
        await using var ctx = harness.CreateContext();
        ctx.AppSettings.Add(new AppSettings
        {
            DefaultExtensionFilter = [], ExcludedPaths = [], ApiToken = "token", SpaceMarginPercent = 5,
        });

        var volume = new Volume
        {
            VolumeGuid = Guid, FileSystem = "exFAT", ScanEngine = VolumeScanEngine.Enumeration, IsOnline = true,
        };
        ctx.Volumes.Add(volume);
        await ctx.SaveChangesAsync();

        ctx.WatchedRoots.Add(new WatchedRoot { VolumeId = volume.Id, RelativePath = "", IsActive = true });
        await ctx.SaveChangesAsync();
        return volume.Id;
    }

    private static async Task ScanAsync(
        SqliteInMemoryContext harness, int volumeId, List<ScanEntry> entries, int batchSize = 5_000)
    {
        await using var ctx = harness.CreateContext();
        var sut = new ScanService(ctx,
            new FakeVolumeProbe(new ProbedVolume(
                Guid, "SER", "Disk", "exFAT", IsRemovable: false,
                MountPoints: [@"X:\"], CapacityBytes: 5000, FreeBytes: 2000, PhysicalDiskId: null)),
            new FakeUsnReader([], 0),
            new FakeDirectoryEnumerator(entries),
            new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
            new BulkIndexWriter(ctx),
            new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
            new FileSearchIndex(ctx),
            new FakeNotificationPublisher(),
            new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System),
            NullLogger<ScanService>.Instance)
        {
            FileBatchSize = batchSize,
        };

        await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<int>> SearchAsync(SqliteInMemoryContext harness, string text)
    {
        await using var ctx = harness.CreateContext();
        var result = await new FileSearchIndex(ctx).SearchAsync(
            new FileSearchQuery(text, SearchScope.Name, null, null, null, null, null, null, null, false,
                SearchSort.Relevance, false, 0, 50),
            CancellationToken.None);
        return result.Items;
    }
}
