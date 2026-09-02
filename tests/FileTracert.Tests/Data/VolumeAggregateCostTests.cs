using FileTracert.Business.Dashboard;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace FileTracert.Tests.Data;

/// <summary>
/// Step 14c — the two Volumes screens against the real 742 033-file catalog cost 1 571 ms (list)
/// and 1 768 ms (detail), while a Dashboard that aggregates the SAME table costs 373 ms. The
/// difference was never the data, it was the shape of the query: the list scanned
/// <c>IX_Files_VolumeId_DirectoryId</c> and then fetched the table row of every file in the catalog
/// to read two booleans — the shape step 11e found one level down, on the Catalog counters.
///
/// <para><b>What is asserted, and why it is the plan.</b> "The count never leaves the index" is a
/// statement about the PLAN, and it cannot be measured with the row-visit counter of 11e/14a: that
/// counter lives in a view over <c>Files</c>, and a view forces the table access whose absence is
/// the whole point. So the plan is read back from the SQL the product really emitted
/// (<see cref="CapturingSqliteConnection"/>), never from a copy pasted into the test — a copy would
/// only prove the copy is planned well.</para>
///
/// <para>Equivalence sits next to cost, because cheaper is only a fix if the answer does not move:
/// the counters are compared against the same numbers computed straight from the entities, in the
/// cases 11h made distinct (an excluded file counts for neither, an absent one for neither, and a
/// directory that exists on disk counts even when nothing under it is indexed).</para>
/// </summary>
public sealed class VolumeAggregateCostTests : IAsyncLifetime
{
    private const int IncludedPerVolume = 4_000;
    private const int ExcludedPerVolume = 500;
    private const int Volumes = 6;

    private readonly ITestOutputHelper _out;
    private CapturingSqliteConnection _connection = null!;
    private SqliteInMemoryContext _harness = null!;
    private FileTracertDbContext _ctx = null!;
    private readonly List<int> _volumeIds = [];
    private readonly Dictionary<int, int> _rootByVolume = [];

    public VolumeAggregateCostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _connection = new CapturingSqliteConnection("Data Source=:memory:");
        _harness = new SqliteInMemoryContext(connection: _connection);
        _ctx = _harness.CreateContext();

        for (var i = 0; i < Volumes; i++)
        {
            var id = await AddVolumeAsync();
            _volumeIds.Add(id);
            await SeedFilesAsync(id, IncludedPerVolume, 1 + i * 100_000, included: true);
            await SeedFilesAsync(id, ExcludedPerVolume, 50_001 + i * 100_000, included: false);
        }
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

    /// <summary>
    /// Raw SQL, not entities: change-tracking tens of thousands of rows is itself the slow part of
    /// a test that is about a query.
    /// </summary>
    private async Task SeedFilesAsync(int volumeId, int count, int firstIndex, bool included)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff");
        var inc = included ? 1 : 0;
        var byType = included ? 0 : 1;
        // Every interpolated value is an int or a value produced right here — nothing user-supplied.
#pragma warning disable EF1002
        await _ctx.Database.ExecuteSqlRawAsync($"""
            WITH RECURSIVE seq(n) AS (
                SELECT {firstIndex} UNION ALL SELECT n + 1 FROM seq WHERE n < {firstIndex + count - 1})
            INSERT INTO Files
              (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
               CreatedUtc, ModifiedUtc, Attributes, IsIncluded, ExcludedByType, ExcludedByRoot,
               ExcludedByScan, ExcludedByPath, IsPresent, LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
            SELECT {volumeId}, {_rootByVolume[volumeId]}, 'f' || n || '.bin', 'bin', 'Other', 1024,
                   '{now}', '{now}', 0, {inc}, {byType}, 0, 0, 0, 1, '{now}', 'None', '{now}', '{now}'
            FROM seq
            """);
#pragma warning restore EF1002
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    /// <summary>The plan SQLite makes for the last statement the product emitted containing <paramref name="fragment"/>.</summary>
    private async Task<List<string>> ExplainLastAsync(string fragment)
    {
        var statement = _connection.Statements
            .Last(s => s.Sql.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + statement.Sql;
        foreach (var (name, value) in statement.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var plan = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        _connection.Reset();
        return plan;
    }

    private IQueryable<FileEntry> IndexedFiles() => _ctx.Files.Where(f => f.IsIncluded && f.IsPresent);

    // ── 1. cost ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_counts_every_volume_without_leaving_the_index()
    {
        _connection.Reset();
        await VolumeFileCounts.ComputeAsync(IndexedFiles(), CancellationToken.None);
        var plan = await ExplainLastAsync("GROUP BY");

        foreach (var line in plan)
        {
            _out.WriteLine(line);
        }

        plan.Should().Contain(l => l.Contains("COVERING INDEX", StringComparison.Ordinal),
            "otherwise SQLite fetches the table row of every file in the catalog to read two " +
            "booleans — which is what made this endpoint cost 1 571 ms on 742 033 files");
        plan.Should().NotContain(l => l.Contains("TEMP B-TREE", StringComparison.Ordinal),
            "grouping by VolumeId must be satisfied by the index order, not by sorting the catalog");
    }

    [Fact]
    public async Task The_detail_aggregates_one_volume_without_leaving_the_index()
    {
        _connection.Reset();
        await CatalogTotals.ComputeAsync(
            IndexedFiles().Where(f => f.VolumeId == _volumeIds[2]), CancellationToken.None);
        var plan = await ExplainLastAsync("SELECT");

        foreach (var line in plan)
        {
            _out.WriteLine(line);
        }

        plan.Should().Contain(l => l.Contains("COVERING INDEX", StringComparison.Ordinal),
            "the byte total must come out of the index too, or the seek is followed by a row " +
            "lookup per file of the volume");
    }

    // ── 2. equivalence: the same numbers ──────────────────────────────────────

    [Fact]
    public async Task The_counters_say_exactly_what_the_catalog_says()
    {
        var counts = await VolumeFileCounts.ComputeAsync(IndexedFiles(), CancellationToken.None);

        counts.Should().HaveCount(Volumes);

        foreach (var id in _volumeIds)
        {
            // Compared against the answer read straight off Files, not against the seeding
            // constants: both aggregates take their filter from the call site, so the realistic way
            // this breaks is BOTH acquiring the same wrong predicate — and a constant on the right
            // of the assertion would agree with them.
            var expectedCount = await _ctx.Files
                .CountAsync(f => f.VolumeId == id && f.IsIncluded && f.IsPresent);
            var expectedBytes = await _ctx.Files
                .Where(f => f.VolumeId == id && f.IsIncluded && f.IsPresent)
                .SumAsync(f => f.SizeBytes);

            counts[id].Should().Be(expectedCount);

            var totals = await CatalogTotals.ComputeAsync(
                IndexedFiles().Where(f => f.VolumeId == id), CancellationToken.None);

            totals.TotalFiles.Should().Be(expectedCount);
            totals.TotalBytes.Should().Be(expectedBytes);
        }

        // And the seeded shape is what the test believes it is — an excluded file is not part of
        // the index count (11h), so the two numbers must differ by exactly the excluded ones.
        counts.Values.Should().AllSatisfy(c => c.Should().Be(IncludedPerVolume));
        (await _ctx.Files.CountAsync(f => !f.IsIncluded))
            .Should().Be(ExcludedPerVolume * Volumes);
    }

    /// <summary>
    /// The two facts 11h separated, checked against the aggregate rather than described: an absent
    /// file leaves the count, an excluded one leaves it too — and they are different reasons, so a
    /// row can be excluded and still present.
    /// </summary>
    [Fact]
    public async Task Absent_and_excluded_leave_the_count_by_different_doors()
    {
        var volumeId = _volumeIds[0];

        await _ctx.Files
            .Where(f => f.VolumeId == volumeId && f.IsIncluded && f.IsPresent)
            .Take(10)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsPresent, false));

        var counts = await VolumeFileCounts.ComputeAsync(IndexedFiles(), CancellationToken.None);
        counts[volumeId].Should().Be(IncludedPerVolume - 10);

        var totals = await CatalogTotals.ComputeAsync(
            IndexedFiles().Where(f => f.VolumeId == volumeId), CancellationToken.None);
        totals.TotalFiles.Should().Be(IncludedPerVolume - 10);
        totals.TotalBytes.Should().Be((IncludedPerVolume - 10) * 1024L);

        // The excluded rows seeded up front are still present on disk, and still uncounted.
        var stillThere = await _ctx.Files
            .CountAsync(f => f.VolumeId == volumeId && !f.IsIncluded && f.IsPresent);
        stillThere.Should().Be(ExcludedPerVolume);
    }

    /// <summary>
    /// A volume with nothing indexed must appear with a zero, not be missing: the list renders one
    /// row per volume and reads the count out of this dictionary.
    /// </summary>
    [Fact]
    public async Task A_volume_with_no_indexed_files_simply_has_no_entry()
    {
        var empty = await AddVolumeAsync();

        var counts = await VolumeFileCounts.ComputeAsync(IndexedFiles(), CancellationToken.None);

        counts.Should().NotContainKey(empty);
        counts.GetValueOrDefault(empty).Should().Be(0,
            "which is how the controller reads it — the row shows a zero, it does not disappear");
    }
}
