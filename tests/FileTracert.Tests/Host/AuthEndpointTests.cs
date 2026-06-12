using System.Net;
using System.Net.Http.Json;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Host;

public sealed class AuthEndpointTests
{
    private const string Header = "X-FileTracert-Token";

    private static FileTracertAppFactory NewFactory() => new()
    {
        Seed = async (db, ct) =>
        {
            db.Volumes.Add(new Volume
            {
                VolumeGuid = @"\\?\Volume{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}\",
                FileSystem = "NTFS",
                Label = "Seeded",
                ScanEngine = VolumeScanEngine.UsnJournal,
                IsOnline = true,
            });
            await db.SaveChangesAsync(ct);
        },
    };

    [Fact]
    public async Task Health_without_token_is_401()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_with_token_is_200()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, factory.Token);

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        body!.Status.Should().Be("running");
        body.Database.Should().Be("ok");
    }

    [Fact]
    public async Task Wrong_token_is_401()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "not-the-token");

        var response = await client.GetAsync("/volumes");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Volumes_with_token_returns_seeded_volume()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, factory.Token);

        var volumes = await client.GetFromJsonAsync<List<VolumeDto>>("/volumes");

        volumes.Should().ContainSingle();
        volumes![0].Label.Should().Be("Seeded");
        volumes[0].ScanEngine.Should().Be("UsnJournal");
    }
}
