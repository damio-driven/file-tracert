using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Realtime;
using FileTracert.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Host;

/// <summary>
/// Step 10b, end to end: a REAL SignalR client (<see cref="HubConnectionBuilder"/>) over the
/// in-memory <see cref="TestServer"/>, so the negotiate handshake, the token guard and the
/// serialization are the ones production uses.
///
/// Transport is long polling because <see cref="TestServer"/> has no real socket to upgrade;
/// everything under test here — the auth gate, the hub mapping, the payload shape — is
/// transport-independent, and the query-string token exists precisely because the transport that
/// production uses (WebSocket) cannot carry a header.
/// </summary>
public sealed class RealtimeHubTests
{
    private const int VolumeId = 1;
    private const string VolumeGuid = @"\\?\Volume{eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee}\";

    private static FileTracertAppFactory NewFactory() => new()
    {
        // These tests are about the hub: nothing else may write to the DB underneath them.
        DisableVolumeSync = true,
        DisableScan = true,
        DisableQueue = true,
        DisableDeviceWatcher = true,
        Seed = async (db, ct) =>
        {
            db.Volumes.Add(new Volume
            {
                Id = VolumeId,
                VolumeGuid = VolumeGuid,
                FileSystem = "NTFS",
                Label = "Hub",
                ScanEngine = VolumeScanEngine.UsnJournal,
                IsOnline = true,
                FreeBytesLastKnown = 1_000_000,
            });
            await db.SaveChangesAsync(ct);
        },
    };

    [Fact]
    public async Task Without_a_token_the_hub_refuses_the_connection()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();          // forces the host to start
        await using var connection = BuildConnection(factory, token: null);

        var connecting = async () => await connection.StartAsync();

        (await connecting.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task With_a_wrong_token_the_hub_refuses_the_connection()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        await using var connection = BuildConnection(factory, token: "not-the-token");

        var connecting = async () => await connection.StartAsync();

        (await connecting.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task With_the_token_in_the_query_string_the_connection_is_established()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        await using var connection = BuildConnection(factory, factory.Token);

        await connection.StartAsync();

        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Enqueuing_a_job_over_the_API_pushes_JobStateChanged()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-FileTracert-Token", factory.Token);

        await using var connection = BuildConnection(factory, factory.Token);
        var received = Listen(connection, RealtimeMethods.JobStateChanged);
        await connection.StartAsync();

        var response = await client.PostAsJsonAsync("/api/operations/enqueue", new CreateJobRequest
        {
            Type = JobType.CreateFolder,
            TargetVolumeId = VolumeId,
            TargetRelativePath = "Nuova",
        });
        response.EnsureSuccessStatusCode();
        var job = (await response.Content.ReadFromJsonAsync<OperationJobDto>())!;

        var message = await NextAsync(received);
        message.GetProperty("jobId").GetInt32().Should().Be(job.Id);
        // Enums travel as names, like the Web API: 10c types them as a TS union, not a number.
        message.GetProperty("state").GetString().Should().Be(nameof(JobState.Pending));
    }

    [Fact]
    public async Task A_published_notification_pushes_NotificationRaised()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        await using var connection = BuildConnection(factory, factory.Token);
        var received = Listen(connection, RealtimeMethods.NotificationRaised);
        await connection.StartAsync();

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationPublisher>().PublishAsync(
                NotificationSeverity.Warning, "Test", "Titolo", "dettaglio", VolumeId, CancellationToken.None);
        }

        var message = await NextAsync(received);
        message.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
        message.GetProperty("severity").GetString().Should().Be(nameof(NotificationSeverity.Warning));
        message.GetProperty("title").GetString().Should().Be("Titolo");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static HubConnection BuildConnection(FileTracertAppFactory factory, string? token)
    {
        var server = factory.Server;
        var url = new UriBuilder(new Uri(server.BaseAddress, "hubs/events"))
        {
            Query = token is null ? string.Empty : $"access_token={Uri.EscapeDataString(token)}",
        }.Uri;

        return new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    /// <summary>
    /// Subscribes BEFORE the connection starts and buffers into a channel, so a message that
    /// arrives faster than the test can await it is never lost. Received as raw JSON on purpose:
    /// asserting on the wire shape is what proves the enum-as-string contract.
    /// </summary>
    private static ChannelReader<JsonElement> Listen(HubConnection connection, string method)
    {
        var channel = Channel.CreateUnbounded<JsonElement>();
        connection.On<JsonElement>(method, message => channel.Writer.TryWrite(message));
        return channel.Reader;
    }

    private static async Task<JsonElement> NextAsync(ChannelReader<JsonElement> reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadAsync(timeout.Token);
    }
}
