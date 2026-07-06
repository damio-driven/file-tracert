using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Logging;
using FileTracert.Contracts.Paging;
using FileTracert.Data.Logging;
using FileTracert.Host.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace FileTracert.Tests.Host;

/// <summary>
/// The queued SQLite logging provider: records reach the store, a sink failure is
/// swallowed (logging never crashes the app), and the runtime level switch gates
/// what gets captured.
/// </summary>
public sealed class SqliteLoggingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ft-logprov-{Guid.NewGuid():N}.db");
    private readonly SqliteLogStore _store;

    public SqliteLoggingTests()
    {
        SQLitePCL.Batteries.Init();
        _store = new SqliteLogStore($"Data Source={_dbPath}");
        _store.EnsureSchema();
    }

    [Fact]
    public async Task Logged_entry_with_exception_is_persisted()
    {
        await using var processor = new SqliteLogProcessor(_store);
        var provider = new SqliteLoggerProvider(processor, new LogLevelSwitch(LogLevel.Trace));
        var logger = provider.CreateLogger("Test.Category");

        logger.LogError(new InvalidOperationException("boom"), "scan {VolumeId} failed", 7);

        await TestPolling.WaitUntilAsync(async () => (await Query()).TotalCount == 1);
        var page = await Query();
        var entry = page.Items.Single();
        entry.Level.Should().Be("Error");
        entry.Category.Should().Be("Test.Category");
        entry.Message.Should().Be("scan 7 failed");
        entry.Exception.Should().Contain("InvalidOperationException").And.Contain("boom");
    }

    [Fact]
    public async Task Sink_failure_never_throws_to_the_caller()
    {
        await using var processor = new SqliteLogProcessor(new ThrowingLogStore());
        var provider = new SqliteLoggerProvider(processor, new LogLevelSwitch(LogLevel.Trace));
        var logger = provider.CreateLogger("Test");

        var act = () => logger.LogInformation("still fine");

        act.Should().NotThrow();
        // Give the consumer a moment to swallow the sink failure; nothing observable.
        await Task.Delay(200);
    }

    [Fact]
    public void Logger_is_gated_by_the_runtime_level_switch()
    {
        var levelSwitch = new LogLevelSwitch(LogLevel.Warning);
        var provider = new SqliteLoggerProvider(new SqliteLogProcessor(_store), levelSwitch);
        var logger = provider.CreateLogger("Test");

        logger.IsEnabled(LogLevel.Information).Should().BeFalse();
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();

        levelSwitch.Current = LogLevel.Debug;

        logger.IsEnabled(LogLevel.Information).Should().BeTrue();
    }

    private Task<PagedResult<LogEntryDto>> Query() =>
        _store.QueryAsync(new LogQuery(0, 50), CancellationToken.None);

    private sealed class ThrowingLogStore : ILogStore
    {
        public void EnsureSchema() { }

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct) =>
            throw new InvalidOperationException("disk full");

        public Task<PagedResult<LogEntryDto>> QueryAsync(LogQuery query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> TrimAsync(DateTime olderThanUtc, int maxRows, bool vacuum, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CheckpointAsync(CancellationToken ct) => throw new NotSupportedException();
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
