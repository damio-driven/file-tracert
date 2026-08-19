using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Logging;
using FileTracert.Contracts.Paging;
using FileTracert.Data.Logging;
using FileTracert.Host.Logging;
using FileTracert.Tests.Infrastructure;
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
        _store = new SqliteLogStore(SqliteTestDatabase.ConnectionString(_dbPath));
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
        // C24: swallowed for the caller, but never unrecorded — the records the sink could
        // not write are counted so the loss is readable instead of invisible.
        await TestPolling.WaitUntilAsync(() => Task.FromResult(processor.FailedRecordCount >= 1));
        processor.FailedRecordCount.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>C24: a record the queue cannot even accept is counted too, not lost in silence.</summary>
    [Fact]
    public async Task Records_dropped_by_a_full_queue_are_counted()
    {
        // Capacity 1 with a store that never returns: the consumer takes the first record and
        // stalls inside the write, so everything after it hits a full channel.
        var store = new BlockingLogStore();
        await using var processor = new SqliteLogProcessor(store, capacity: 1, batchSize: 1);
        try
        {
            var provider = new SqliteLoggerProvider(processor, new LogLevelSwitch(LogLevel.Trace));
            var logger = provider.CreateLogger("Test");

            logger.LogInformation("first");
            await TestPolling.WaitUntilAsync(() => Task.FromResult(store.Entered));

            for (var i = 0; i < 20; i++)
            {
                logger.LogInformation("overflow {I}", i);
            }

            processor.DroppedRecordCount.Should().BeGreaterThan(0);
        }
        finally
        {
            // Always let the consumer out, or disposing the processor would wait on it forever.
            store.Release();
        }
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

    /// <summary>
    /// Releases only this store's own pool: clearing every pool in the process would dispose
    /// the native handle of whatever another test class is querying (see
    /// <see cref="SqliteTestDatabase"/>).
    /// </summary>
    public void Dispose() => SqliteTestDatabase.Delete(_dbPath);
}
