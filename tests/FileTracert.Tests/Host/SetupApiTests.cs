using System.Net;
using System.Net.Http.Json;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Business;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FileTracert.Tests.Host;

public sealed class SetupApiTests
{
    private const string Header = "X-FileTracert-Token";
    private const string OnlineGuid = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";

    private static ProbedVolume Probed() => new(
        OnlineGuid, "SER", "Disk", "NTFS", IsRemovable: false,
        MountPoints: [@"X:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null);

    private static FileTracertAppFactory NewFactory(
        bool online = true,
        IReadOnlyDictionary<string, IReadOnlyList<FolderNode>>? folders = null) => new()
    {
        DisableVolumeSync = true,
        DisableScan = true,
        Probe = online ? new FakeVolumeProbe(Probed()) : new FakeVolumesProbe([]),
        FileSystemBrowser = new FakeFileSystemBrowser(
            folders ?? new Dictionary<string, IReadOnlyList<FolderNode>>
            {
                [""] = [new FolderNode("Foto", "Foto", true), new FolderNode("Video", "Video", false)],
            }),
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
        var settings = await db.AppSettings.FirstAsync(ct);
        settings.DefaultExtensionFilter = ["jpg"];
        var volume = new Volume
        {
            VolumeGuid = OnlineGuid, Label = "Disk", FileSystem = "NTFS",
            IsOnline = true, CapacityBytes = 1000, FreeBytesLastKnown = 500,
            LastSeenUtc = DateTime.UtcNow, ScanEngine = VolumeScanEngine.UsnJournal,
        };
        db.Volumes.Add(volume);
        await db.SaveChangesAsync(ct);

        var dir = new DirectoryNode { VolumeId = volume.Id, Name = "Foto", MaterializedPath = "Foto", IsMaterialized = true };
        db.Directories.Add(dir);
        await db.SaveChangesAsync(ct);
        db.Files.AddRange(
            NewFile(volume.Id, dir.Id, "a.jpg", "jpg", included: true),
            NewFile(volume.Id, dir.Id, "b.png", "png", included: false));
        await db.SaveChangesAsync(ct);
    }

    private static FileEntry NewFile(int vol, int dir, string name, string ext, bool included) => new()
    {
        VolumeId = vol, DirectoryId = dir, Name = name, Extension = ext,
        Category = FileCategory.Image, SizeBytes = 10, IsIncluded = included, IsPresent = true,
        LastIndexedUtc = DateTime.UtcNow,
    };

    private static async Task<int> VolumeIdAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<List<VolumeDto>>("/api/volumes") ?? [];
        return list.Single().Id;
    }

    [Fact]
    public async Task Browse_online_returns_folders()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);
        var id = await VolumeIdAsync(client);

        var folders = await client.GetFromJsonAsync<PagedResult<FolderNodeDto>>($"/api/volumes/{id}/folders?path=");

        folders!.Items.Select(f => f.Name).Should().ContainInOrder("Foto", "Video");
        folders.TotalCount.Should().Be(2);
    }

    /// <summary>
    /// Step 17: the disk decides how many folders a level holds, so the browse answer is paged
    /// like every other list. The browser already sorts by name; the page follows that order.
    /// </summary>
    [Fact]
    public async Task Browse_pages_the_folders_of_one_level()
    {
        // Handed out of order on purpose: the page order is the SERVICE's contract, not something
        // it may trust a port implementation to have done (review of step 17).
        using var factory = NewFactory(folders: new Dictionary<string, IReadOnlyList<FolderNode>>
        {
            [""] = [new FolderNode("c", "c", true), new FolderNode("a", "a", false), new FolderNode("b", "b", false)],
        });
        using var client = Authed(factory);
        var id = await VolumeIdAsync(client);

        var first = await client.GetFromJsonAsync<PagedResult<FolderNodeDto>>($"/api/volumes/{id}/folders?path=&take=2");
        first!.Items.Select(f => f.Name).Should().Equal("a", "b");
        first.TotalCount.Should().Be(3);
        first.Skip.Should().Be(0);
        first.Take.Should().Be(2);

        var last = await client.GetFromJsonAsync<PagedResult<FolderNodeDto>>($"/api/volumes/{id}/folders?path=&skip=2&take=2");
        last!.Items.Select(f => f.Name).Should().Equal("c");
        last.Items[0].HasChildren.Should().BeTrue();

        var capped = await client.GetFromJsonAsync<PagedResult<FolderNodeDto>>($"/api/volumes/{id}/folders?path=&take=100000");
        capped!.Take.Should().Be(PagedRequest.MaxTake);
    }

    [Fact]
    public async Task Browse_offline_is_409()
    {
        using var factory = NewFactory(online: false);
        using var client = Authed(factory);
        var id = await VolumeIdAsync(client);

        var response = await client.GetAsync($"/api/volumes/{id}/folders?path=");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Browse_traversal_path_is_400()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);
        var id = await VolumeIdAsync(client);

        var response = await client.GetAsync($"/api/volumes/{id}/folders?path=..%5CWindows");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_then_nested_is_409()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);
        var id = await VolumeIdAsync(client);

        var created = await client.PostAsJsonAsync($"/api/volumes/{id}/watched-roots",
            new CreateWatchedRootRequest("Foto", null));
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var nested = await client.PostAsJsonAsync($"/api/volumes/{id}/watched-roots",
            new CreateWatchedRootRequest("Foto\\2024", null));
        nested.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_bad_path_is_400()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);
        var id = await VolumeIdAsync(client);

        var response = await client.PostAsJsonAsync($"/api/volumes/{id}/watched-roots",
            new CreateWatchedRootRequest("C:\\Foto", null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_override_to_all_types_includes_png_and_flags_scan()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);
        var id = await VolumeIdAsync(client);

        var created = await (await client.PostAsJsonAsync($"/api/volumes/{id}/watched-roots",
            new CreateWatchedRootRequest("Foto", null))).Content.ReadFromJsonAsync<WatchedRootDto>();

        var patch = await client.PatchAsJsonAsync($"/api/watched-roots/{created!.Id}",
            new UpdateWatchedRootRequest(IsActive: null, FilterOverride: new FilterOverrideDto(UseDefault: false, Extensions: [])));
        var body = await patch.Content.ReadFromJsonAsync<WatchedRootUpdateResponse>();

        body!.Reconcile!.NeedsScan.Should().BeTrue();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        (await db.Files.CountAsync(f => f.IsIncluded)).Should().Be(2);
    }

    [Fact]
    public async Task Delete_removes_root_and_soft_excludes_files()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);
        var id = await VolumeIdAsync(client);

        var created = await (await client.PostAsJsonAsync($"/api/volumes/{id}/watched-roots",
            new CreateWatchedRootRequest("Foto", null))).Content.ReadFromJsonAsync<WatchedRootDto>();

        var response = await client.DeleteAsync($"/api/watched-roots/{created!.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        (await db.WatchedRoots.CountAsync()).Should().Be(0);
        (await db.Files.CountAsync()).Should().Be(2);
        (await db.Files.CountAsync(f => f.IsIncluded)).Should().Be(0);
    }

    [Fact]
    public async Task Get_and_put_filter_settings()
    {
        using var factory = NewFactory();
        using var client = Authed(factory);

        var current = await client.GetFromJsonAsync<FilterSettingsDto>("/api/settings/filter");
        current!.AllowedExtensions.Should().Contain("jpg");

        var put = await client.PutAsJsonAsync("/api/settings/filter",
            new FilterSettingsDto(AllowedExtensions: ["jpg", "png"], ExcludedPaths: current.ExcludedPaths));
        var reconcile = await put.Content.ReadFromJsonAsync<ReconcileResultDto>();

        reconcile!.NeedsScan.Should().BeTrue();
    }
}
