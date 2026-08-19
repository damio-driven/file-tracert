using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Logging;
using FileTracert.Data.Logging;
using FileTracert.Tests.Infrastructure;
using FluentAssertions;

namespace FileTracert.Tests.Data;

/// <summary>
/// The dedicated SQLite log store: schema bootstrap, batch append, filtered/paged
/// query (newest first), and retention trimming — all over a real temp DB file.
/// </summary>
public sealed class SqliteLogStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ft-logs-{Guid.NewGuid():N}.db");
    private readonly SqliteLogStore _store;

    public SqliteLogStoreTests()
    {
        SQLitePCL.Batteries.Init();
        _store = new SqliteLogStore($"Data Source={_dbPath}");
        _store.EnsureSchema();
    }

    private static LogRecord Record(
        int level,
        string message,
        DateTime at,
        string category = "Test",
        string? exception = null) =>
        new(at, level, category, message, exception, EventId: null, Scope: null);

    [Fact]
    public async Task WriteBatch_then_query_returns_records_newest_first()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.WriteBatchAsync(
            [
                Record(2, "first", t0),
                Record(4, "second", t0.AddMinutes(1), exception: "System.Exception: boom\n   at X"),
            ],
            CancellationToken.None);

        var page = await _store.QueryAsync(new LogQuery(Skip: 0, Take: 50), CancellationToken.None);

        page.TotalCount.Should().Be(2);
        page.Items.Should().HaveCount(2);
        page.Items[0].Message.Should().Be("second");
        page.Items[0].Level.Should().Be("Error");
        page.Items[0].Exception.Should().Contain("boom");
        page.Items[1].Message.Should().Be("first");
        page.Items[1].Level.Should().Be("Information");
    }

    [Fact]
    public async Task Query_filters_by_minimum_level()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.WriteBatchAsync(
            [
                Record(0, "trace", t0),
                Record(2, "info", t0.AddSeconds(1)),
                Record(4, "error", t0.AddSeconds(2)),
            ],
            CancellationToken.None);

        var page = await _store.QueryAsync(
            new LogQuery(Skip: 0, Take: 50, MinLevel: 3),
            CancellationToken.None);

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(e => e.Message == "error");
    }

    [Fact]
    public async Task Query_filters_by_search_over_message_and_exception()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.WriteBatchAsync(
            [
                Record(2, "nothing here", t0),
                Record(2, "scan finished", t0.AddSeconds(1)),
                Record(4, "boom", t0.AddSeconds(2), exception: "ACCESS_DENIED while scanning"),
            ],
            CancellationToken.None);

        var page = await _store.QueryAsync(
            new LogQuery(Skip: 0, Take: 50, Search: "scan"),
            CancellationToken.None);

        page.TotalCount.Should().Be(2);
        page.Items.Select(e => e.Message).Should().BeEquivalentTo(["scan finished", "boom"]);
    }

    /// <summary>
    /// C28: the search text is a literal, not a pattern. '%' and '_' are LIKE wildcards, and a
    /// user hunting for "100%" or "file_name" would otherwise get whatever happened to match.
    /// </summary>
    [Theory]
    [InlineData("100%", "disk at 100% capacity", "counter reached 100 items")]
    [InlineData("file_name", "bad file_name rejected", "bad fileXname rejected")]
    public async Task Query_search_treats_LIKE_wildcards_as_literals(string search, string hit, string miss)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.WriteBatchAsync(
            [Record(2, hit, t0), Record(2, miss, t0.AddSeconds(1))],
            CancellationToken.None);

        var page = await _store.QueryAsync(
            new LogQuery(Skip: 0, Take: 50, Search: search),
            CancellationToken.None);

        page.TotalCount.Should().Be(1);
        page.Items.Single().Message.Should().Be(hit);
    }

    /// <summary>The escape character itself must not go through raw either.</summary>
    [Fact]
    public async Task Query_search_matches_a_literal_backslash()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.WriteBatchAsync(
            [
                Record(2, @"copying C:\Temp\a.jpg", t0),
                Record(2, "copying C:/Temp/a.jpg", t0.AddSeconds(1)),
            ],
            CancellationToken.None);

        var page = await _store.QueryAsync(
            new LogQuery(Skip: 0, Take: 50, Search: @"C:\Temp"),
            CancellationToken.None);

        page.TotalCount.Should().Be(1);
        page.Items.Single().Message.Should().Be(@"copying C:\Temp\a.jpg");
    }

    [Fact]
    public async Task Writes_are_visible_to_a_separate_store_instance_on_the_same_file()
    {
        // Mirrors production: the logger processor writes through one path while the
        // API reads through another — separate connections must see committed rows.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.WriteBatchAsync([Record(2, "shared", t0)], CancellationToken.None);

        var reader = new SqliteLogStore($"Data Source={_dbPath}");
        var page = await reader.QueryAsync(new LogQuery(0, 50), CancellationToken.None);

        page.Items.Should().ContainSingle(e => e.Message == "shared");
    }

    [Fact]
    public async Task Query_filters_by_category_prefix()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.WriteBatchAsync(
            [
                Record(2, "a", t0, category: "FileTracert.Host"),
                Record(3, "b", t0.AddSeconds(1), category: "FileTracert.Business"),
                Record(2, "c", t0.AddSeconds(2), category: "Microsoft.Hosting"),
            ],
            CancellationToken.None);

        // Prefix match: "FileTracert" matches both FileTracert.* categories.
        var prefix = await _store.QueryAsync(
            new LogQuery(Skip: 0, Take: 50, Category: "FileTracert"),
            CancellationToken.None);
        prefix.TotalCount.Should().Be(2);
        prefix.Items.Select(e => e.Message).Should().BeEquivalentTo(["a", "b"]);

        // A deeper prefix narrows further.
        var narrow = await _store.QueryAsync(
            new LogQuery(Skip: 0, Take: 50, Category: "FileTracert.Host"),
            CancellationToken.None);
        narrow.Items.Should().ContainSingle(e => e.Message == "a");
    }

    [Fact]
    public async Task Query_pages()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var records = Enumerable.Range(0, 5)
            .Select(i => Record(2, $"m{i}", t0.AddSeconds(i)))
            .ToList();
        await _store.WriteBatchAsync(records, CancellationToken.None);

        var page = await _store.QueryAsync(new LogQuery(Skip: 2, Take: 2), CancellationToken.None);

        page.TotalCount.Should().Be(5);
        page.Items.Should().HaveCount(2);
        // newest first: m4, m3, [m2, m1], m0 → skip 2 → m2, m1
        page.Items.Select(e => e.Message).Should().ContainInOrder("m2", "m1");
    }

    [Fact]
    public async Task Trim_removes_rows_older_than_cutoff_and_beyond_cap()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var records = Enumerable.Range(0, 10)
            .Select(i => Record(2, $"m{i}", t0.AddDays(i)))
            .ToList();
        await _store.WriteBatchAsync(records, CancellationToken.None);

        // Keep only entries on/after day 5 → removes m0..m4 (5 rows).
        var removed = await _store.TrimAsync(t0.AddDays(5), maxRows: 1000, vacuum: false, CancellationToken.None);

        removed.Should().Be(5);
        var page = await _store.QueryAsync(new LogQuery(Skip: 0, Take: 50), CancellationToken.None);
        page.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task Trim_caps_total_rows_keeping_newest()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var records = Enumerable.Range(0, 10)
            .Select(i => Record(2, $"m{i}", t0.AddMinutes(i)))
            .ToList();
        await _store.WriteBatchAsync(records, CancellationToken.None);

        var removed = await _store.TrimAsync(DateTime.MinValue, maxRows: 3, vacuum: true, CancellationToken.None);

        removed.Should().Be(7);
        var page = await _store.QueryAsync(new LogQuery(Skip: 0, Take: 50), CancellationToken.None);
        page.Items.Select(e => e.Message).Should().ContainInOrder("m9", "m8", "m7");
        page.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Checkpoint_truncates_a_grown_wal()
    {
        // Grow the WAL: bulk-write with auto-checkpoint disabled so frames accumulate.
        // Keep this connection open across the checkpoint+assert: closing the last connection
        // makes SQLite checkpoint and delete the -wal file, which would hide the effect.
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await Exec(conn, "PRAGMA wal_autocheckpoint=0;");
        var payload = new string('x', 4000);
        for (int i = 0; i < 200; i++)
        {
            await Exec(
                conn,
                "INSERT INTO LogEntries (TimestampUtc, Level, Category, Message) " +
                $"VALUES ('2026-01-01', 2, 'T', '{payload}');");
        }

        var grownWal = new FileInfo(_dbPath + "-wal").Length;
        grownWal.Should().BeGreaterThan(100_000);

        // The idle connection holds no read lock, so TRUNCATE can merge every frame back.
        await _store.CheckpointAsync(CancellationToken.None);

        new FileInfo(_dbPath + "-wal").Length.Should().BeLessThan(grownWal / 10);
    }

    private static async Task Exec(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Releases only this store's own pool: clearing every pool in the process would dispose
    /// the native handle of whatever another test class is querying (see
    /// <see cref="SqliteTestDatabase"/>).
    /// </summary>
    public void Dispose() => SqliteTestDatabase.Delete(_dbPath);
}
