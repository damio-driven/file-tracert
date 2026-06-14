using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Logging;
using FileTracert.Data.Logging;
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

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
