using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Logging;
using FileTracert.Contracts.Paging;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Host.Controllers;
using FileTracert.Host.Logging;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Tests.Host;

/// <summary>
/// Unit coverage of <see cref="LogsController"/>: how query parameters are translated
/// into a <see cref="LogQuery"/> (level name → integer floor, paging normalization)
/// and how the level endpoints validate and persist.
/// </summary>
public sealed class LogsControllerTests
{
    private sealed class CapturingLogStore : ILogStore
    {
        public LogQuery? LastQuery { get; private set; }

        public void EnsureSchema() { }

        public Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct) => Task.CompletedTask;

        public Task<PagedResult<LogEntryDto>> QueryAsync(LogQuery query, CancellationToken ct)
        {
            LastQuery = query;
            return Task.FromResult(new PagedResult<LogEntryDto>([], 0, query.Skip, query.Take));
        }

        public Task<int> TrimAsync(DateTime olderThanUtc, int maxRows, bool vacuum, CancellationToken ct) =>
            Task.FromResult(0);

        public Task CheckpointAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Get_maps_level_name_to_integer_floor_and_passes_filters()
    {
        var store = new CapturingLogStore();
        using var ctx = new SqliteInMemoryContext();
        var controller = new LogsController(store, new LogLevelSwitch(), ctx.CreateContext());

        await controller.Get(
            skip: 10,
            take: 25,
            level: "Warning",
            category: "Cat.A",
            search: "boom",
            fromUtc: null,
            toUtc: null);

        store.LastQuery.Should().NotBeNull();
        store.LastQuery!.MinLevel.Should().Be(3);     // Warning
        store.LastQuery.Category.Should().Be("Cat.A");
        store.LastQuery.Search.Should().Be("boom");
        store.LastQuery.Skip.Should().Be(10);
        store.LastQuery.Take.Should().Be(25);
    }

    [Fact]
    public async Task Get_normalizes_oversized_take_and_blank_filters()
    {
        var store = new CapturingLogStore();
        using var ctx = new SqliteInMemoryContext();
        var controller = new LogsController(store, new LogLevelSwitch(), ctx.CreateContext());

        await controller.Get(skip: -5, take: 100_000, level: "  ", category: "", search: null);

        store.LastQuery!.Skip.Should().Be(0);
        store.LastQuery.Take.Should().Be(PagedRequest.MaxTake);
        store.LastQuery.MinLevel.Should().BeNull();
        store.LastQuery.Category.Should().BeNull();
        store.LastQuery.Search.Should().BeNull();
    }

    [Fact]
    public void Get_level_reports_the_runtime_switch()
    {
        using var ctx = new SqliteInMemoryContext();
        var controller = new LogsController(
            new CapturingLogStore(), new LogLevelSwitch(LogLevel.Debug), ctx.CreateContext());

        var result = controller.GetLevel().Result as OkObjectResult;

        result!.Value.Should().BeOfType<LogLevelDto>().Which.Level.Should().Be("Debug");
    }

    [Fact]
    public async Task Set_level_rejects_unknown_value()
    {
        using var ctx = new SqliteInMemoryContext();
        var controller = new LogsController(new CapturingLogStore(), new LogLevelSwitch(), ctx.CreateContext());

        var result = await controller.SetLevel(new LogLevelDto("Loud"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Set_level_updates_switch_and_persists_settings()
    {
        using var ctx = new SqliteInMemoryContext();
        await using (var seed = ctx.CreateContext())
        {
            seed.AppSettings.Add(new AppSettings { Id = 1, ApiToken = "t" });
            await seed.SaveChangesAsync();
        }

        var levelSwitch = new LogLevelSwitch();
        await using var db = ctx.CreateContext();
        var controller = new LogsController(new CapturingLogStore(), levelSwitch, db);

        var result = await controller.SetLevel(new LogLevelDto("error"), CancellationToken.None);

        (result.Result as OkObjectResult)!.Value.Should().BeOfType<LogLevelDto>()
            .Which.Level.Should().Be("Error");
        levelSwitch.Current.Should().Be(LogLevel.Error);

        await using var verify = ctx.CreateContext();
        (await verify.AppSettings.AsNoTracking().FirstAsync()).MinimumLogLevel.Should().Be("Error");
    }
}
