using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

/// <summary>
/// Step 14a — a structural filter must cost what the RESULT costs, not what the MATCH SET costs.
///
/// <para>The defect, as the step 13 soak found it on 742 033 real files: the FTS index produced
/// the rowids of every match and only then was each row resolved on <c>Files</c> and thrown away
/// if its category did not fit. Searching <c>report</c> took 517 ms and returned 431 rows;
/// <c>report</c> filtered to <c>Image</c> returned <b>two</b> rows and took <b>12 seconds</b>, and
/// <c>e</c> filtered to <c>Image</c> never came back at all. The more selective the filter, the
/// worse it got — the opposite of what pressing a category chip should do.</para>
///
/// <para>What is asserted here is WORK, not milliseconds: <c>Files</c> is renamed aside and a view
/// of the same name takes its place carrying <c>visit(Id)</c> in its WHERE, so every candidate row
/// the statement steps through is counted. It is the technique of
/// <see cref="SearchCountCapTests.Production_count_stops_at_the_cap"/>, pointed at the other half
/// of the same statement. Before the fix the number follows the seeded match set; after it, the
/// result.</para>
///
/// <para>Equivalence is asserted next to cost, because cheaper is only a fix if the answer does
/// not move: the same rows, in the same order, for every combination of filters.</para>
/// </summary>
public sealed class SearchFilterCostTests : IAsyncLifetime
{
    private SqliteInMemoryContext _harness = null!;
    private FileTracertDbContext _ctx = null!;
    private FileSearchIndex _fts = null!;
    private int _volumeId;
    private int _otherVolumeId;

    public Task InitializeAsync()
    {
        _harness = new SqliteInMemoryContext();
        _ctx = _harness.CreateContext();
        SqliteFts.Create(_ctx);
        _fts = new FileSearchIndex(_ctx);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _harness.Dispose();
        return Task.CompletedTask;
    }

    // ── seeding ───────────────────────────────────────────────────────────────

    private async Task<int> AddVolumeAsync()
    {
        var volume = new Volume
        {
            VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
            IsOnline = true,
        };
        _ctx.Volumes.Add(volume);
        await _ctx.SaveChangesAsync();

        var root = new DirectoryNode
        {
            VolumeId = volume.Id,
            Name = string.Empty,
            MaterializedPath = string.Empty,
            IsMaterialized = true,
        };
        _ctx.Directories.Add(root);
        await _ctx.SaveChangesAsync();
        _rootByVolume[volume.Id] = root.Id;
        return volume.Id;
    }

    private readonly Dictionary<int, int> _rootByVolume = [];

    /// <summary>
    /// Seeds <paramref name="count"/> indexable files that all share the token "match", each in
    /// <paramref name="category"/>. Raw SQL because EF change tracking over tens of thousands of
    /// entities is itself the slow part of a test that is about a query.
    /// </summary>
    private async Task SeedAsync(int volumeId, FileCategory category, string extension, int count, int firstIndex)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff");
        var rootId = _rootByVolume[volumeId];

        // Every interpolated value is an int, or a value this method produced — nothing user-supplied.
#pragma warning disable EF1002
        await _ctx.Database.ExecuteSqlRawAsync($"""
            WITH RECURSIVE seq(n) AS (
                SELECT {firstIndex} UNION ALL SELECT n + 1 FROM seq WHERE n < {firstIndex + count - 1})
            INSERT INTO Files
              (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
               CreatedUtc, ModifiedUtc, Attributes, IsIncluded, ExcludedByType, ExcludedByRoot,
               ExcludedByScan, ExcludedByPath, IsPresent, LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
            SELECT {volumeId}, {rootId}, 'match' || n || '.{extension}', '{extension}',
                   '{category}', 1024 * n,
                   '{now}', '{now}', 0, 1, 0, 0, 0, 0, 1, '{now}', 'None', '{now}', '{now}'
            FROM seq
            """);
#pragma warning restore EF1002
    }

    private FileSearchQuery Query(
        FileCategory? category = null,
        int? volumeId = null,
        string[]? extensions = null,
        long? sizeMin = null,
        SearchScope scope = SearchScope.Name,
        SearchSort sort = SearchSort.Name,
        int take = 20) =>
        new("match", scope, category, extensions, sizeMin, null, null, null,
            volumeId, false, sort, false, 0, take);

    // ── 1. the defect: cost follows the match set ─────────────────────────────

    /// <summary>
    /// 5 000 files match the text; 5 of them are the category asked for. The old shape resolved
    /// all 5 000 on <c>Files</c> to keep 5.
    /// </summary>
    [Fact]
    public async Task Category_filter_visits_the_result_not_the_match_set()
    {
        _volumeId = await AddVolumeAsync();
        await SeedAsync(_volumeId, FileCategory.Other, "bin", count: 5_000, firstIndex: 1);
        await SeedAsync(_volumeId, FileCategory.Image, "jpg", count: 5, firstIndex: 100_001);
        await _fts.SyncVolumeFromDbAsync(_volumeId, CancellationToken.None);

        var visits = await MeasureAsync(() => _fts.SearchAsync(Query(FileCategory.Image), CancellationToken.None));

        visits.Result.TotalCount.Should().Be(5);
        visits.Result.Items.Should().HaveCount(5);

        // The count and the page statement each step the surviving rows once, plus whatever the
        // planner does to satisfy ORDER BY. Bounded well under the match set rather than pinned to
        // an exact number, so a future SQLite that plans it differently reports a planner change,
        // not a regression in the thing under test. Today: 10. Before the fix: 10 000.
        visits.Visits.Should().BeLessThan(100,
            "the filter must be answered by the index, not by resolving all 5 000 matches on Files");
    }

    /// <summary>Same argument for the volume filter, which the Search screen also exposes.</summary>
    [Fact]
    public async Task Volume_filter_visits_the_result_not_the_match_set()
    {
        _volumeId = await AddVolumeAsync();
        _otherVolumeId = await AddVolumeAsync();
        await SeedAsync(_volumeId, FileCategory.Other, "bin", count: 5_000, firstIndex: 1);
        await SeedAsync(_otherVolumeId, FileCategory.Other, "bin", count: 5, firstIndex: 100_001);
        await _fts.SyncVolumeFromDbAsync(_volumeId, CancellationToken.None);
        await _fts.SyncVolumeFromDbAsync(_otherVolumeId, CancellationToken.None);

        var visits = await MeasureAsync(() => _fts.SearchAsync(Query(volumeId: _otherVolumeId), CancellationToken.None));

        visits.Result.TotalCount.Should().Be(5);
        visits.Visits.Should().BeLessThan(100,
            "the volume filter must be answered by the index, not by resolving all 5 005 matches");
    }

    // ── 2. equivalence: the same rows, in the same order ──────────────────────

    /// <summary>
    /// Every filter combination the API can produce, checked against the answer computed straight
    /// from <c>Files</c>. The point is not that the numbers are pretty — it is that moving a filter
    /// into the index did not move a single row.
    /// </summary>
    [Fact]
    public async Task Every_filter_combination_returns_the_same_rows_as_the_catalog_says()
    {
        _volumeId = await AddVolumeAsync();
        _otherVolumeId = await AddVolumeAsync();
        await SeedAsync(_volumeId, FileCategory.Image, "jpg", count: 40, firstIndex: 1);
        await SeedAsync(_volumeId, FileCategory.Document, "pdf", count: 30, firstIndex: 101);
        await SeedAsync(_otherVolumeId, FileCategory.Image, "png", count: 20, firstIndex: 201);
        await SeedAsync(_otherVolumeId, FileCategory.Video, "mp4", count: 10, firstIndex: 301);
        await _fts.SyncVolumeFromDbAsync(_volumeId, CancellationToken.None);
        await _fts.SyncVolumeFromDbAsync(_otherVolumeId, CancellationToken.None);

        var cases = new (string Label, FileSearchQuery Query)[]
        {
            ("no filter",              Query()),
            ("category",               Query(category: FileCategory.Image)),
            ("volume",                 Query(volumeId: _volumeId)),
            ("category + volume",      Query(category: FileCategory.Image, volumeId: _otherVolumeId)),
            ("extension",              Query(extensions: ["jpg", "mp4"])),
            ("category + extension",   Query(category: FileCategory.Image, extensions: ["png"])),
            ("category + size",        Query(category: FileCategory.Image, sizeMin: 1024 * 20)),
            ("all four",               Query(FileCategory.Image, _volumeId, ["jpg"], 1024 * 10)),
            ("category, no match",     Query(category: FileCategory.Audio)),
            ("full path scope",        Query(category: FileCategory.Image, scope: SearchScope.FullPath)),
            ("relevance sort",         Query(category: FileCategory.Image, sort: SearchSort.Relevance)),
        };

        foreach (var (label, query) in cases)
        {
            var actual = await _fts.SearchAsync(query, CancellationToken.None);
            var expected = await ExpectedIdsAsync(query);

            actual.TotalCount.Should().Be(expected.Count, "total for '{0}'", label);
            actual.Items.Should().BeEquivalentTo(
                expected.Take(query.Take),
                o => o.WithStrictOrdering(),
                "rows for '{0}'", label);
        }
    }

    /// <summary>
    /// The answer computed without the index at all: the same predicates read straight off
    /// <c>Files</c>. Relevance ordering has no equivalent here, so those cases are compared as a
    /// set — which still catches a row appearing or disappearing, which is what the fix could
    /// break.
    /// </summary>
    private async Task<List<int>> ExpectedIdsAsync(FileSearchQuery q)
    {
        var rows = _ctx.Files
            .AsNoTracking()
            .Where(f => f.IsIncluded && f.IsPresent && f.Name.StartsWith(q.Text));

        if (q.Category.HasValue) rows = rows.Where(f => f.Category == q.Category.Value);
        if (q.VolumeId.HasValue) rows = rows.Where(f => f.VolumeId == q.VolumeId.Value);
        if (q.Extensions is { Length: > 0 }) rows = rows.Where(f => q.Extensions.Contains(f.Extension));
        if (q.SizeBytesMin.HasValue) rows = rows.Where(f => f.SizeBytes >= q.SizeBytesMin.Value);

        var ordered = q.Sort switch
        {
            SearchSort.Size => rows.OrderBy(f => f.SizeBytes),
            _ => rows.OrderBy(f => f.Name),
        };

        var ids = await ordered.Select(f => f.Id).ToListAsync();

        // Relevance is bm25's business, not this method's: compare the same rows, and let the
        // ordering assertion below fall back to the index's own order for that one case.
        if (q.Sort == SearchSort.Relevance)
        {
            var actual = await _fts.SearchAsync(q, CancellationToken.None);
            ids = [.. actual.Items.Where(ids.Contains), .. ids.Where(id => !actual.Items.Contains(id))];
        }

        return ids;
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="search"/> with <c>Files</c> shadowed by a view that carries a counting
    /// UDF in its WHERE, so the number reported is exactly how many candidate rows the two
    /// statements of one <c>SearchAsync</c> stepped through.
    /// </summary>
    private async Task<(PagedResult<int> Result, int Visits)> MeasureAsync(
        Func<Task<PagedResult<int>>> search)
    {
        var conn = (SqliteConnection)_ctx.Database.GetDbConnection();
        await _ctx.Database.OpenConnectionAsync();

        int visits = 0;
        conn.CreateFunction("visit", (long _) => { visits++; return 1L; });

        await _ctx.Database.ExecuteSqlRawAsync("ALTER TABLE Files RENAME TO FilesReal");
        await _ctx.Database.ExecuteSqlRawAsync(
            "CREATE VIEW Files AS SELECT * FROM FilesReal WHERE visit(Id) = 1");
        try
        {
            var result = await search();
            return (result, visits);
        }
        finally
        {
            await _ctx.Database.ExecuteSqlRawAsync("DROP VIEW Files");
            await _ctx.Database.ExecuteSqlRawAsync("ALTER TABLE FilesReal RENAME TO Files");
            _ctx.Database.CloseConnection();
        }
    }
}
