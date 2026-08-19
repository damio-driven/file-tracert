using System.Diagnostics;
using FileTracert.Contracts.Logging;
using FileTracert.Host.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileTracert.Tests.Host;

/// <summary>
/// C23: the queued log records must reach the store when the host stops, and the wait for
/// them must be bounded. Both are asserted against a real host running the same registration
/// <c>Program</c> uses — the defect was never in the processor but in nobody calling it.
/// </summary>
public sealed class LogShutdownDrainTests
{
    /// <summary>Slow enough that "drained" cannot be mistaken for "the consumer kept up".</summary>
    private static readonly TimeSpan WritePerBatch = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Records_still_queued_when_the_host_stops_reach_the_store()
    {
        var store = new SlowRecordingLogStore(WritePerBatch);
        var host = BuildHost(store, TimeSpan.FromSeconds(10));
        await host.StartAsync();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FileTracert.Test.Drain");
        for (var i = 0; i < 50; i++)
        {
            logger.LogInformation("record {Index}", i);
        }

        // No pause: the point is that these are still in flight when the stop begins.
        await host.StopAsync();

        store.Written
            .Count(r => r.Category == "FileTracert.Test.Drain")
            .Should().Be(50, "the stop must not leave the queue behind");

        host.Dispose();
    }

    [Fact]
    public async Task A_store_that_never_returns_does_not_hold_the_shutdown()
    {
        var cap = TimeSpan.FromSeconds(1);
        var host = BuildHost(new HangingLogStore(), cap);
        await host.StartAsync();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FileTracert.Test.Hang");
        for (var i = 0; i < 10; i++)
        {
            logger.LogInformation("record {Index}", i);
        }

        var elapsed = Stopwatch.StartNew();
        await host.StopAsync();
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThan(
            cap + TimeSpan.FromSeconds(4), "the drain is capped; a stuck sink must not become a stuck service");

        // Giving up is not the same as pretending: the abandoned records are counted.
        host.Services.GetRequiredService<SqliteLogProcessor>().FailedRecordCount.Should().BeGreaterThan(0);

        host.Dispose();
    }

    /// <summary>
    /// The drain is registered first so that it stops last — that is the whole reason a worker's
    /// goodbye still reaches the log DB. Asserted rather than assumed: the host's stop order is
    /// a framework behaviour this design leans on.
    /// </summary>
    [Fact]
    public async Task A_worker_stopping_after_the_flush_is_registered_still_reaches_the_store()
    {
        var store = new SlowRecordingLogStore(WritePerBatch);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.AddSqliteLogging(store, new LogLevelSwitch(LogLevel.Trace), TimeSpan.FromSeconds(10));
        // Registered after the flush service, therefore stopped before it.
        builder.Services.AddHostedService<GoodbyeWorker>();

        var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();

        store.Written.Should().Contain(r => r.Message == GoodbyeWorker.Goodbye);

        host.Dispose();
    }

    /// <summary>A minimal host wired through the very registration the composition root uses.</summary>
    private static IHost BuildHost(ILogStore store, TimeSpan drainTimeout)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.AddSqliteLogging(store, new LogLevelSwitch(LogLevel.Trace), drainTimeout);
        return builder.Build();
    }

    private sealed class GoodbyeWorker(ILogger<GoodbyeWorker> logger) : IHostedService
    {
        public const string Goodbye = "worker says goodbye";

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation(Goodbye);
            return Task.CompletedTask;
        }
    }
}
