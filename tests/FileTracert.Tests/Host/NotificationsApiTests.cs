using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Paging;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Host;

/// <summary>Integration coverage of the notifications API: list, unread count, read, dismiss.</summary>
public sealed class NotificationsApiTests
{
    private const string Header = "X-FileTracert-Token";

    private static readonly DateTime T = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    // The API serializes enums as names; the client must read them the same way.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static Task<PagedResult<NotificationDto>?> ListAsync(HttpClient client, string url) =>
        client.GetFromJsonAsync<PagedResult<NotificationDto>>(url, JsonOpts);

    private static FileTracertAppFactory NewFactory() => new()
    {
        DisableVolumeSync = true,
        DisableScan = true,
        Seed = SeedAsync,
    };

    private static HttpClient Authed(FileTracertAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, factory.Token);
        return client;
    }

    private static async Task SeedAsync(FileTracertDbContext db, CancellationToken ct)
    {
        db.Notifications.AddRange(
            new Notification { TimestampUtc = T, Severity = NotificationSeverity.Info, Source = "Scan", Title = "old", Message = "m1", IsRead = true },
            new Notification { TimestampUtc = T.AddMinutes(1), Severity = NotificationSeverity.Warning, Source = "Scan", Title = "mid", Message = "m2" },
            new Notification { TimestampUtc = T.AddMinutes(2), Severity = NotificationSeverity.Error, Source = "Scan", Title = "new", Message = "m3" },
            new Notification { TimestampUtc = T.AddMinutes(3), Severity = NotificationSeverity.Info, Source = "Scan", Title = "gone", Message = "m4", IsDismissed = true });
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task Get_returns_non_dismissed_newest_first()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var page = await ListAsync(client, "/api/notifications");

        page!.TotalCount.Should().Be(3);   // dismissed excluded
        page.Items.Select(n => n.Title).Should().ContainInOrder("new", "mid", "old");
    }

    [Fact]
    public async Task Get_unread_filters_to_unread()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var page = await ListAsync(client, "/api/notifications?unread=true");

        page!.Items.Select(n => n.Title).Should().BeEquivalentTo(["mid", "new"]);
    }

    [Fact]
    public async Task Unread_count_excludes_read_and_dismissed()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var count = await client.GetFromJsonAsync<NotificationCountDto>("/api/notifications/unread-count");

        count!.Unread.Should().Be(2);
    }

    [Fact]
    public async Task Mark_read_drops_it_from_unread()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var unread = await ListAsync(client, "/api/notifications?unread=true");
        var target = unread!.Items.Single(n => n.Title == "new");

        var response = await client.PostAsync($"/api/notifications/{target.Id}/read", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var count = await client.GetFromJsonAsync<NotificationCountDto>("/api/notifications/unread-count");
        count!.Unread.Should().Be(1);
    }

    [Fact]
    public async Task Dismiss_hides_it_from_the_list()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var list = await ListAsync(client, "/api/notifications");
        var target = list!.Items.Single(n => n.Title == "mid");

        var response = await client.PostAsync($"/api/notifications/{target.Id}/dismiss", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await ListAsync(client, "/api/notifications");
        after!.Items.Select(n => n.Title).Should().NotContain("mid");
        after.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Read_unknown_id_is_404()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var response = await client.PostAsync("/api/notifications/9999/read", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
