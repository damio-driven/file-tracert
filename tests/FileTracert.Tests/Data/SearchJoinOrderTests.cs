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
/// Step 14a, second half — the search must always be driven BY THE INDEX, whatever filter it
/// carries and whatever the planner feels like doing.
///
/// <para>The first half moved category and volume into the MATCH, where the index answers them.
/// The filters that cannot become tokens — extension (its text does not survive the tokenizer
/// intact), size and date (ranges) — stay ordinary SQL predicates, and that leaves the door open
/// to the shape that made the soak's worst case unbounded: given an equality predicate on an
/// indexed column, SQLite reverses the join order, drives from <c>Files</c>, and asks the FTS
/// table "does rowid X match?" once per candidate row. Each of those questions re-runs the
/// full-text query — for a prefix term, a full doclist merge — so the cost is not "match set
/// times a constant", it is "match set times the whole query, again".</para>
///
/// <para>Measured on the real 742 033-file catalog, filtering on an extension that matched
/// nothing: <b>739 ms</b> with the planner free to choose, <b>10 ms</b> with the order pinned. The
/// answer is identical; only the direction changes.</para>
///
/// <para>This is asserted on the PLAN, not on a clock and not on rows visited, because rows
/// visited cannot see it: the flipped shape touches FEWER rows of <c>Files</c> and pays for it
/// inside the virtual table, where SQLite exposes no counter. The plan is read back from the SQL
/// the product really issued (<see cref="CapturingSqliteConnection"/>) rather than from a copy
/// pasted into the test, which would only prove the copy is planned well.</para>
/// </summary>
public sealed class SearchJoinOrderTests : IAsyncLifetime
{
    private CapturingSqliteConnection _connection = null!;
    private SqliteInMemoryContext _harness = null!;
    private FileTracertDbContext _ctx = null!;
    private FileSearchIndex _fts = null!;
    private int _volumeId;

    public async Task InitializeAsync()
    {
        _connection = new CapturingSqliteConnection("Data Source=:memory:");
        _harness = new SqliteInMemoryContext(connection: _connection);
        _ctx = _harness.CreateContext();
        SqliteFts.Create(_ctx);
        _fts = new FileSearchIndex(_ctx);

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
        // Every interpolated value is an int, or a value this method produced.
#pragma warning disable EF1002
        await _ctx.Database.ExecuteSqlRawAsync($"""
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 5000)
            INSERT INTO Files
              (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
               CreatedUtc, ModifiedUtc, Attributes, IsIncluded, ExcludedByType, ExcludedByRoot,
               ExcludedByScan, IsPresent, LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
            SELECT {volume.Id}, {root.Id}, 'match' || n || '.bin', 'bin', 'Other', 1024 * n,
                   '{now}', '{now}', 0, 1, 0, 0, 0, 1, '{now}', 'None', '{now}', '{now}'
            FROM seq
            """);
#pragma warning restore EF1002
        await _fts.SyncVolumeFromDbAsync(volume.Id, CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _harness.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// An extension filter is the case that still reaches SQL, so it is the one that can flip.
    /// Every statement the search issues must plan the FTS table as its outermost loop.
    /// </summary>
    [Fact]
    public async Task Every_search_statement_is_driven_by_the_index()
    {
        var query = new FileSearchQuery(
            "match", SearchScope.Name, null, ["jpg"], null, null, null, null,
            null, false, SearchSort.Name, false, 0, 20);

        _connection.Reset();
        await _fts.SearchAsync(query, CancellationToken.None);

        var searchStatements = _connection.Statements
            .Where(s => s.Sql.Contains("FileSearchIndex MATCH", StringComparison.Ordinal))
            .ToList();

        searchStatements.Should().HaveCount(2, "SearchAsync issues one count and one page statement");

        foreach (var statement in searchStatements)
        {
            var plan = await ExplainAsync(statement);
            OutermostLoop(plan).Should().Contain("fts",
                "the index must drive the join; the plan was:\n{0}", string.Join("\n", plan));
        }
    }

    /// <summary>
    /// The plan of a statement, as SQLite reports it, re-bound with the parameter values the
    /// statement actually ran with — a plan explained with different values is a different plan.
    /// </summary>
    private async Task<List<string>> ExplainAsync(CapturingSqliteConnection.CapturedStatement statement)
    {
        // A plain connection, so explaining does not record itself.
        await using var cmd = ((SqliteConnection)_ctx.Database.GetDbConnection()).CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + statement.Sql;
        foreach (var (name, value) in statement.Parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        await _ctx.Database.OpenConnectionAsync();
        try
        {
            var rows = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(3));
            return rows;
        }
        finally { _ctx.Database.CloseConnection(); }
    }

    /// <summary>
    /// The first SCAN or SEARCH line of the plan — the table SQLite decided to walk. The
    /// CO-ROUTINE / subquery wrappers that the capped count adds are not loops, so they are
    /// skipped rather than parsed.
    /// </summary>
    private static string OutermostLoop(List<string> plan) =>
        plan.FirstOrDefault(l =>
            l.StartsWith("SCAN ", StringComparison.Ordinal) ||
            l.StartsWith("SEARCH ", StringComparison.Ordinal))
        ?? string.Join(" / ", plan);
}
