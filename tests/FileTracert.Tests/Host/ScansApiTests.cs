using System.Net;
using System.Net.Http.Json;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Host;

/// <summary>
/// Integration coverage of the scan-progress poll endpoint and the manual
/// catalogable override. Workers are disabled so the fake probe doesn't interfere.
/// </summary>
public sealed class ScansApiTests
{
    private const string Header = "X-FileTracert-Token";

    private static FileTracertAppFactory NewFactory() => new()
    {
        DisableVolumeSync = true,
        DisableScan = true,
    };

    private static HttpClient Authed(FileTracertAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, factory.Token);
        return client;
    }

    [Fact]
    public async Task Status_reflects_an_active_scan_and_empties_on_complete()
    {
        using var factory = NewFactory();
        var client = Authed(factory);
        var tracker = factory.Services.GetRequiredService<IScanStatusTracker>();

        tracker.Begin(42, "Disk");
        tracker.ReportSeen(42, 142_000);
        tracker.SetPhase(42, ScanPhase.Writing);

        var active = await client.GetStringAsync("/api/scans/status");
        active.Should().Contain("\"volumeId\":42")
            .And.Contain("\"phase\":\"Writing\"")
            .And.Contain("\"itemsSeen\":142000");

        tracker.Complete(42);

        var empty = await client.GetStringAsync("/api/scans/status");
        empty.Should().Be("[]");
    }

    [Fact]
    public async Task SetCatalogable_overrides_the_flag_in_the_database()
    {
        int volumeId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var volume = new Volume
                {
                    VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
                    Label = "GoogleDrive",
                    FileSystem = "NTFS",
                    Kind = VolumeKind.Cloud,
                    IsCatalogable = false,
                    IsOnline = true,
                };
                db.Volumes.Add(volume);
                await db.SaveChangesAsync(ct);
                volumeId = volume.Id;
            },
        };
        var client = Authed(factory);

        // Re-enable a false positive.
        var resp = await client.PostAsJsonAsync($"/api/volumes/{volumeId}/catalogable", new SetCatalogableRequest(true));
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            (await db.Volumes.SingleAsync()).IsCatalogable.Should().BeTrue();
        }

        // Unknown volume → 404.
        var missing = await client.PostAsJsonAsync("/api/volumes/9999/catalogable", new SetCatalogableRequest(true));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
