using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Host;

/// <summary>
/// Integration coverage of the step-6 domain API over a real SQLite DB. Workers are
/// disabled so the empty fake probe doesn't flip the seeded volumes offline.
/// </summary>
public sealed class DomainApiTests
{
    private const string Header = "X-FileTracert-Token";

    private static FileTracertAppFactory NewFactory(string environment = "Development") => new()
    {
        EnvironmentName = environment,
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
        // Two online volumes (labels chosen to test the alphabetical secondary sort)
        // and one offline volume whose figures are a last-known snapshot.
        var alpha = NewVolume("Alpha", online: true);
        var bravo = NewVolume("Bravo", online: true);
        var charlie = NewVolume("Charlie", online: false, freeBytes: 128, lastUsn: 44_821_330);
        charlie.WatchedRoots.Add(new WatchedRoot { RelativePath = @"\Foto", IsActive = true });

        db.Volumes.AddRange(alpha, bravo, charlie);
        await db.SaveChangesAsync(ct);

        AddFiles(db, alpha, 2);   // 2 included files
        AddFiles(db, bravo, 1);
        AddFiles(db, charlie, 1);
        // An excluded file must not count toward totals.
        AddFiles(db, alpha, 1, included: false);
        await db.SaveChangesAsync(ct);
    }

    private static Volume NewVolume(string label, bool online, long freeBytes = 500, long? lastUsn = null) => new()
    {
        VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
        Label = label,
        FileSystem = "NTFS",
        IsRemovable = false,
        IsOnline = online,
        CapacityBytes = 1_000,
        FreeBytesLastKnown = freeBytes,
        LastSeenUtc = DateTime.UtcNow,
        ScanEngine = VolumeScanEngine.UsnJournal,
        LastUsn = lastUsn,
    };

    private static void AddFiles(FileTracertDbContext db, Volume volume, int count, bool included = true)
    {
        var dir = new DirectoryNode
        {
            VolumeId = volume.Id,
            Name = "root",
            MaterializedPath = string.Empty,
            IsMaterialized = true,
        };
        db.Directories.Add(dir);
        db.SaveChanges();

        for (var i = 0; i < count; i++)
        {
            db.Files.Add(new FileEntry
            {
                VolumeId = volume.Id,
                DirectoryId = dir.Id,
                Name = $"f{i}.jpg",
                Extension = "jpg",
                Category = FileCategory.Image,
                SizeBytes = 100,
                IsIncluded = included,
                IsPresent = true,
                LastIndexedUtc = DateTime.UtcNow,
            });
        }
    }

    [Fact]
    public async Task Dashboard_aggregates_index_and_volumes()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var stats = await client.GetFromJsonAsync<DashboardStatsDto>("/api/dashboard");

        stats!.TotalFiles.Should().Be(4);            // excluded file not counted
        stats.TotalBytes.Should().Be(400);
        stats.VolumesOnline.Should().Be(2);
        stats.VolumesTotal.Should().Be(3);
        stats.QueuedJobs.Should().Be(0);
    }

    [Fact]
    public async Task Volumes_list_orders_online_first_then_label_with_counts()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var volumes = await client.GetFromJsonAsync<List<VolumeDto>>("/api/volumes") ?? [];

        volumes.Select(v => v.Label).Should().ContainInOrder("Alpha", "Bravo", "Charlie");

        var alpha = volumes.Single(v => v.Label == "Alpha");
        alpha.FileCount.Should().Be(2);   // excluded file not counted
        alpha.DataIsLive.Should().BeTrue();

        var charlie = volumes.Single(v => v.Label == "Charlie");
        charlie.DataIsLive.Should().BeFalse();
        charlie.FreeBytes.Should().Be(128);
    }

    [Fact]
    public async Task Volume_detail_returns_roots_and_index_stats()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var list = await client.GetFromJsonAsync<List<VolumeDto>>("/api/volumes") ?? [];
        var charlieId = list.Single(v => v.Label == "Charlie").Id;

        var detail = await client.GetFromJsonAsync<VolumeDetailDto>($"/api/volumes/{charlieId}");

        detail!.ScanEngine.Should().Be("UsnJournal");
        detail.LastUsn.Should().Be(44_821_330);
        detail.FileCount.Should().Be(1);
        detail.IndexedBytes.Should().Be(100);
        detail.WatchedRoots.Should().ContainSingle();
        detail.WatchedRoots[0].RelativePath.Should().Be(@"\Foto");
        detail.WatchedRoots[0].EffectiveFilter.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Volume_detail_unknown_id_is_404()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var response = await client.GetAsync("/api/volumes/9999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rescan_existing_volume_is_202()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var list = await client.GetFromJsonAsync<List<VolumeDto>>("/api/volumes") ?? [];
        var id = list[0].Id;

        var response = await client.PostAsync($"/api/volumes/{id}/rescan", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Rescan_unknown_volume_is_404()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var response = await client.PostAsync("/api/volumes/9999/rescan", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dev_token_endpoint_exists_in_development()
    {
        using var factory = NewFactory("Development");
        using var client = factory.CreateClient(); // no token: endpoint is whitelisted

        var response = await client.GetAsync("/api/dev/token");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DevToken>();
        body!.Token.Should().Be(factory.Token);
    }

    [Fact]
    public async Task Dev_token_endpoint_absent_in_production()
    {
        using var factory = NewFactory("Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/dev/token");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Every DB-sourced timestamp must reach the client with the UTC designator:
    /// without it the browser parses the string as local time and every relative
    /// clock in the UI is off by the machine offset (review finding #12).
    /// </summary>
    [Fact]
    public async Task Volume_timestamps_serialize_with_the_utc_designator()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var json = await client.GetStringAsync("/api/volumes");

        using var document = JsonDocument.Parse(json);
        var lastSeen = document.RootElement[0].GetProperty("lastSeenUtc").GetString();

        lastSeen.Should().EndWith("Z");
    }

    private sealed record DevToken(string Token);
}
