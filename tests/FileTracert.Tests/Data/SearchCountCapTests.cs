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
/// E3 — the search total is capped at 10 000, and the cap has to bound the WORK, not just the
/// number printed. <c>SELECT MIN(COUNT(*), 10000)</c> clamps after visiting every match plus two
/// joins per match; <c>SELECT COUNT(*) FROM (SELECT 1 … LIMIT 10000)</c> makes SQLite stop
/// stepping at the cap.
///
/// Two things are proved here, because "less work" is only an optimisation if the answer does
/// not move:
/// <list type="number">
/// <item><b>Same output</b> — the production <see cref="FileSearchIndex.SearchAsync"/> returns the
/// same total below, at, and above the cap.</item>
/// <item><b>Less work, measured</b> — both spellings are run over the same data with a SQLite
/// user-defined function wired into the predicate, which counts exactly how many candidate rows
/// each one steps through. Milliseconds are noise; this is a count.</item>
/// </list>
/// </summary>
public sealed class SearchCountCapTests : IAsyncLifetime
{
    private const int Cap = 10_000;

    private SqliteInMemoryContext _harness = null!;
    private FileTracertDbContext _ctx = null!;
    private FileSearchIndex _fts = null!;
    private int _volumeId;

    public async Task InitializeAsync()
    {
        _harness = new SqliteInMemoryContext();
        _ctx = _harness.CreateContext();

        SqliteFts.Create(_ctx);

        _fts = new FileSearchIndex(_ctx);
    }

    public Task DisposeAsync()
    {
        _harness.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> indexable files whose names all share the token "match".
    /// Rows are inserted with raw SQL: EF change tracking over tens of thousands of entities is
    /// itself the slow part of a test that is about a query.
    /// </summary>
    private async Task SeedMatchingFilesAsync(int count)
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
        _volumeId = volume.Id;

        var root = new DirectoryNode
        {
            VolumeId = volume.Id,
            Name = string.Empty,
            MaterializedPath = string.Empty,
            IsMaterialized = true,
        };
        _ctx.Directories.Add(root);
        await _ctx.SaveChangesAsync();

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff");
        // Every interpolated value is an int or a value this method produced — nothing user-supplied.
#pragma warning disable EF1002
        await _ctx.Database.ExecuteSqlRawAsync($"""
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {count})
            INSERT INTO Files
              (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
               CreatedUtc, ModifiedUtc, Attributes, IsIncluded, ExcludedByType, ExcludedByRoot,
               ExcludedByScan, IsPresent, LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
            SELECT {volume.Id}, {root.Id}, 'match' || n || '.jpg', 'jpg', 'Image', 1024,
                   '{now}', '{now}', 0, 1, 0, 0, 0, 1, '{now}', 'None', '{now}', '{now}'
            FROM seq
            """);
#pragma warning restore EF1002

        await _fts.SyncVolumeFromDbAsync(volume.Id, CancellationToken.None);
    }

    private FileSearchQuery Query() =>
        new("match", SearchScope.Name, null, null, null, null, null, null,
            _volumeId, false, SearchSort.Name, false, 0, 20);

    // ── 1. same output ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(50)]              // well below the cap
    [InlineData(Cap)]             // exactly at the cap
    [InlineData(Cap + 2_500)]     // above it — the total saturates, it does not overflow
    public async Task Total_matches_the_capped_count_at_every_size(int seeded)
    {
        await SeedMatchingFilesAsync(seeded);

        var result = await _fts.SearchAsync(Query(), CancellationToken.None);

        result.TotalCount.Should().Be(Math.Min(seeded, Cap));
        result.Items.Should().HaveCount(20);
    }

    // ── 2. less work, counted ─────────────────────────────────────────────────

    /// <summary>
    /// The two spellings, run over the same rows with the same predicate, differing only in where
    /// the cap sits. <c>visit(f.Id)</c> is a UDF that returns 1 and increments a counter, so the
    /// number it reports is exactly how many candidate rows the statement stepped through.
    /// </summary>
    [Fact]
    public async Task Capped_subquery_visits_the_cap_where_the_clamped_count_visits_everything()
    {
        const int seeded = Cap + 5_000;
        await SeedMatchingFilesAsync(seeded);

        var conn = (SqliteConnection)_ctx.Database.GetDbConnection();
        await _ctx.Database.OpenConnectionAsync();

        int visits = 0;
        conn.CreateFunction("visit", (long _) => { visits++; return 1L; });

        const string body = """
            FROM FileSearchIndex fts
            JOIN Files f ON f.Id = fts.rowid
            JOIN Volumes v ON v.Id = f.VolumeId
            WHERE FileSearchIndex MATCH 'name : "match"*'
              AND f.IsIncluded = 1 AND f.IsPresent = 1
              AND visit(f.Id) = 1
            """;

        var beforeSql = $"SELECT MIN(COUNT(*), {Cap}) {body}";
        var afterSql = $"SELECT COUNT(*) FROM (SELECT 1 {body} LIMIT {Cap})";

        var (beforeTotal, beforeVisits) = await RunAsync(conn, beforeSql, () => visits, () => visits = 0);
        var (afterTotal, afterVisits) = await RunAsync(conn, afterSql, () => visits, () => visits = 0);

        // Same answer …
        beforeTotal.Should().Be(Cap);
        afterTotal.Should().Be(Cap);

        // … reached by stepping through the whole match set before, and exactly the cap after.
        beforeVisits.Should().Be(seeded);   // 15 000
        afterVisits.Should().Be(Cap);      // 10 000
    }

    /// <summary>
    /// The same measurement, but on the statement <see cref="FileSearchIndex.SearchAsync"/> really
    /// issues — so this one goes RED if the production SQL goes back to clamping after the fact.
    ///
    /// SQLite exposes no per-statement work counter to the client, so the row counter is put where
    /// the statement cannot avoid it: <c>Files</c> is renamed aside and a view of the same name
    /// takes its place, carrying <c>visit(Id)</c> in its WHERE. Every candidate row the count steps
    /// through goes through the view, and therefore through the counter. The page query that
    /// follows is measured separately and subtracted, because both run inside one
    /// <c>SearchAsync</c> call.
    /// </summary>
    [Fact]
    public async Task Production_count_stops_at_the_cap()
    {
        const int seeded = Cap + 5_000;
        await SeedMatchingFilesAsync(seeded);

        var conn = (SqliteConnection)_ctx.Database.GetDbConnection();
        await _ctx.Database.OpenConnectionAsync();

        int visits = 0;
        conn.CreateFunction("visit", (long _) => { visits++; return 1L; });

        await _ctx.Database.ExecuteSqlRawAsync("ALTER TABLE Files RENAME TO FilesReal");
        await _ctx.Database.ExecuteSqlRawAsync(
            "CREATE VIEW Files AS SELECT * FROM FilesReal WHERE visit(Id) = 1");
        try
        {
            var result = await _fts.SearchAsync(Query(), CancellationToken.None);
            result.TotalCount.Should().Be(Cap);

            // Both statements of one SearchAsync walk the view. The page query is ORDER BY +
            // LIMIT 20 over the whole match set — it walks all of it either way, and how many
            // times is the planner's business, not this test's — so the assertion is on the half
            // the cap governs: clamping after the fact costs a second full `seeded`, stopping at
            // the cap costs `Cap`. Bounded rather than pinned to an exact number, so a future
            // SQLite that plans the PAGE differently reports a planner change, not a regression
            // in the thing under test. Today's figures: 25 000 here, 30 000 before the fix.
            visits.Should().BeLessThan(seeded * 2,
                "the count must stop at the cap ({0}) instead of walking all {1} matches again",
                Cap, seeded);
        }
        finally
        {
            await _ctx.Database.ExecuteSqlRawAsync("DROP VIEW Files");
            await _ctx.Database.ExecuteSqlRawAsync("ALTER TABLE FilesReal RENAME TO Files");
            _ctx.Database.CloseConnection();
        }
    }

    private static async Task<(long Total, int Visits)> RunAsync(
        SqliteConnection conn, string sql, Func<int> readVisits, Action reset)
    {
        reset();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var total = (long)(await cmd.ExecuteScalarAsync())!;
        return (total, readVisits());
    }
}
