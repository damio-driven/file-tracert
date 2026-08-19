using FileTracert.Business.Dashboard;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Host.Controllers;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace FileTracert.Tests.Business;

/// <summary>
/// E6 — the Dashboard's index figures come from ONE pass over <c>Files</c>, not two.
///
/// Counting the rows and summing their bytes used to be two sequential aggregates over the largest
/// table in the database — the same one the scan is writing to — for one card. On SQLite, which
/// has a single writer, a pass nobody needed is time nobody else can write in.
///
/// Measured in statements, not milliseconds: the counter is a real EF command interceptor, so the
/// number below is what the database was actually asked to do.
/// </summary>
public sealed class DashboardAggregateTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness = new();
    private readonly CountingCommandInterceptor _sql = new();

    public void Dispose() => _harness.Dispose();

    private FileTracertDbContext Db() => _harness.CreateContext(_sql);

    [Fact]
    public async Task The_index_totals_are_one_statement_over_Files()
    {
        using var db = Db();
        await SeedAsync(db, includedFiles: 3, excludedFiles: 2, bytesEach: 100);
        _sql.Reset();

        var totals = await CatalogTotals.ComputeAsync(
            db.Files.Where(f => f.IsIncluded && f.IsPresent), CancellationToken.None);

        totals.TotalFiles.Should().Be(3);
        totals.TotalBytes.Should().Be(300);

        // One. The pair it replaced was a LongCount plus a Sum.
        _sql.CountContaining("FROM \"Files\"").Should().Be(1);
        _sql.Count.Should().Be(1);
    }

    [Fact]
    public async Task The_volume_totals_are_one_statement_over_Volumes()
    {
        using var db = Db();
        await SeedAsync(db, includedFiles: 1, excludedFiles: 0, bytesEach: 1);
        db.Volumes.Add(NewVolume(online: false));
        await db.SaveChangesAsync();
        _sql.Reset();

        var totals = await VolumeTotals.ComputeAsync(db.Volumes, CancellationToken.None);

        totals.Total.Should().Be(2);
        totals.Online.Should().Be(1);
        _sql.Count.Should().Be(1);
    }

    /// <summary>
    /// The old shape guarded the sum behind <c>totalFiles == 0 ? 0 : …</c>, because SUM over no
    /// rows is NULL and does not fit a non-nullable long. The aggregate has to survive the same
    /// case without that crutch — an empty catalog is what a fresh install looks like.
    /// </summary>
    [Fact]
    public async Task An_empty_catalog_answers_zero_rather_than_failing()
    {
        using var db = Db();

        var catalog = await CatalogTotals.ComputeAsync(
            db.Files.Where(f => f.IsIncluded && f.IsPresent), CancellationToken.None);
        var volumes = await VolumeTotals.ComputeAsync(db.Volumes, CancellationToken.None);

        catalog.Should().Be(CatalogTotals.Empty);
        volumes.Should().Be(VolumeTotals.Empty);
    }

    /// <summary>
    /// A catalog whose every file is excluded: the filter matches nothing, so there is no group —
    /// the same NULL-sum case, reached from a table that is not empty.
    /// </summary>
    [Fact]
    public async Task A_catalog_with_nothing_included_answers_zero()
    {
        using var db = Db();
        await SeedAsync(db, includedFiles: 0, excludedFiles: 4, bytesEach: 999);

        var catalog = await CatalogTotals.ComputeAsync(
            db.Files.Where(f => f.IsIncluded && f.IsPresent), CancellationToken.None);

        catalog.Should().Be(CatalogTotals.Empty);
    }

    /// <summary>
    /// The whole endpoint, counted. Five statements for one card strip — count files, sum files,
    /// count volumes, count online volumes, aggregate the queue — are now three, one per table,
    /// and the two that walked <c>Files</c> are one.
    /// </summary>
    [Fact]
    public async Task The_dashboard_endpoint_asks_one_question_per_table()
    {
        using var db = Db();
        await SeedAsync(db, includedFiles: 3, excludedFiles: 1, bytesEach: 50);
        _sql.Reset();

        var result = await new DashboardController(db).Get(CancellationToken.None);

        var stats = (result.Result as OkObjectResult)!.Value as DashboardStatsDto;
        stats!.TotalFiles.Should().Be(3);
        stats.TotalBytes.Should().Be(150);
        stats.VolumesTotal.Should().Be(1);
        stats.VolumesOnline.Should().Be(1);

        _sql.Count.Should().Be(3, "one aggregate per table: Files, Volumes, OperationJobs");
        _sql.CountContaining("FROM \"Files\"").Should().Be(1);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Volume NewVolume(bool online) => new()
    {
        VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
        FileSystem = "NTFS",
        ScanEngine = VolumeScanEngine.UsnJournal,
        IsOnline = online,
    };

    private static async Task SeedAsync(
        FileTracertDbContext db, int includedFiles, int excludedFiles, long bytesEach)
    {
        var volume = NewVolume(online: true);
        db.Volumes.Add(volume);
        await db.SaveChangesAsync();

        var dir = new DirectoryNode
        {
            VolumeId = volume.Id, Name = "", MaterializedPath = "", IsMaterialized = true,
        };
        db.Directories.Add(dir);
        await db.SaveChangesAsync();

        for (int i = 0; i < includedFiles + excludedFiles; i++)
        {
            db.Files.Add(new FileEntry
            {
                VolumeId = volume.Id,
                DirectoryId = dir.Id,
                Name = $"f{i}.jpg",
                Extension = "jpg",
                Category = FileCategory.Image,
                SizeBytes = bytesEach,
                FileCreatedUtc = DateTime.UtcNow,
                FileModifiedUtc = DateTime.UtcNow,
                IsIncluded = i < includedFiles,
                IsPresent = true,
                LastIndexedUtc = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }
}
