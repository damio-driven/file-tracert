using System.Net;
using System.Net.Http.Json;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
using FileTracert.Data;
using FileTracert.Host.Logging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileTracert.Tests.Host;

/// <summary>
/// Integration coverage of the logs API: paged/filtered read over the dedicated log
/// DB and runtime+persistent control of the minimum log level.
/// </summary>
public sealed class LogsApiTests
{
    private const string Header = "X-FileTracert-Token";

    private static FileTracertAppFactory NewFactory(
        Func<FileTracertDbContext, CancellationToken, Task>? seed = null) => new()
    {
        DisableVolumeSync = true,
        DisableScan = true,
        Seed = seed,
    };

    private static HttpClient Authed(FileTracertAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, factory.Token);
        return client;
    }

    [Fact]
    public async Task Get_logs_returns_the_hosts_own_startup_logs_newest_first()
    {
        // End-to-end proof of the pipeline: the host's startup emits real log lines
        // through the SQLite provider; they must be queryable via the API.
        using var factory = NewFactory();
        using var client = Authed(factory);

        PagedResult<LogEntryDto>? page = null;
        await TestPolling.WaitUntilAsync(async () =>
        {
            page = await client.GetFromJsonAsync<PagedResult<LogEntryDto>>("/api/logs?take=200");
            return page!.TotalCount > 0;
        });

        page!.Items.Should().NotBeEmpty();
        page.Items.Select(e => e.TimestampUtc).Should().BeInDescendingOrder();
        page.Items.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Category));
    }

    [Fact]
    public async Task Get_level_returns_current_level()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var dto = await client.GetFromJsonAsync<LogLevelDto>("/api/logs/level");

        dto!.Level.Should().Be("Information");
    }

    [Fact]
    public async Task Put_level_changes_switch_and_persists()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var response = await client.PutAsJsonAsync("/api/logs/level", new LogLevelDto("Debug"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Runtime switch changed immediately.
        factory.Services.GetRequiredService<LogLevelSwitch>().Current.Should().Be(LogLevel.Debug);

        // Persisted in AppSettings.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        (await db.AppSettings.AsNoTracking().FirstAsync()).MinimumLogLevel.Should().Be("Debug");
    }

    [Fact]
    public async Task Put_level_rejects_unknown_value()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var response = await client.PutAsJsonAsync("/api/logs/level", new LogLevelDto("Loud"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Startup_applies_persisted_minimum_level()
    {
        using var factory = NewFactory(async (db, ct) =>
        {
            var settings = await db.AppSettings.FirstAsync(ct);
            settings.MinimumLogLevel = "Warning";
            await db.SaveChangesAsync(ct);
        });

        // Force the host (and its startup initialization) to build.
        _ = factory.Token;

        factory.Services.GetRequiredService<LogLevelSwitch>().Current.Should().Be(LogLevel.Warning);
    }
}
