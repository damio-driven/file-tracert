using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Host;

public sealed class CatalogApiTests
{
    private const string Header = "X-FileTracert-Token";

    // The API serializes enums as names; the client must read them the same way.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static HttpClient Authed(FileTracertAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(Header, f.Token);
        return c;
    }

    [Fact]
    public async Task GetChildren_root_returns_subdirectories_and_files()
    {
        int volumeId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume
                {
                    VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
                    Label = "Cat Disk", FileSystem = "NTFS",
                    Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true,
                };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                var photos = new DirectoryNode { VolumeId = vol.Id, ParentId = root.Id, Name = "Photos", MaterializedPath = "Photos", IsMaterialized = true };
                var docs   = new DirectoryNode { VolumeId = vol.Id, ParentId = root.Id, Name = "Docs",   MaterializedPath = "Docs",   IsMaterialized = true };
                db.Directories.AddRange(photos, docs);
                await db.SaveChangesAsync(ct);

                db.Files.Add(new FileEntry
                {
                    VolumeId = vol.Id, DirectoryId = root.Id,
                    Name = "readme.txt", Extension = "txt", Category = FileCategory.Document,
                    SizeBytes = 100, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                    IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var resp = await client.GetAsync($"/api/catalog/{volumeId}/children");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CatalogChildrenDto>(JsonOpts);
        dto!.Directories.Should().HaveCount(2);
        dto.Directories.Select(d => d.Name).Should().BeEquivalentTo(["Docs", "Photos"]);
        dto.Files.TotalCount.Should().Be(1);
        dto.Files.Items[0].Name.Should().Be("readme.txt");
        dto.VolumeIsOnline.Should().BeTrue();
        dto.VolumeLabel.Should().Be("Cat Disk");
    }

    [Fact]
    public async Task GetChildren_subdirectory_returns_files_in_that_directory()
    {
        int volumeId = 0;
        int photoDirId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume { VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Disk2", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                var photos = new DirectoryNode { VolumeId = vol.Id, ParentId = root.Id, Name = "Photos", MaterializedPath = "Photos", IsMaterialized = true };
                db.Directories.Add(photos);
                await db.SaveChangesAsync(ct);
                photoDirId = photos.Id;

                db.Files.Add(new FileEntry
                {
                    VolumeId = vol.Id, DirectoryId = photos.Id,
                    Name = "beach.jpg", Extension = "jpg", Category = FileCategory.Image,
                    SizeBytes = 2048, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                    IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var resp = await client.GetAsync($"/api/catalog/{volumeId}/children?directoryId={photoDirId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CatalogChildrenDto>(JsonOpts);
        dto!.Files.TotalCount.Should().Be(1);
        dto.Files.Items[0].Name.Should().Be("beach.jpg");
        dto.CurrentDirectoryId.Should().Be(photoDirId);
        dto.CurrentDirectoryPath.Should().Be("Photos");
    }

    [Fact]
    public async Task GetChildren_excludes_non_present_files()
    {
        int volumeId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume { VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Disk3", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                db.Files.Add(new FileEntry { VolumeId = vol.Id, DirectoryId = root.Id, Name = "present.jpg", Extension = "jpg", Category = FileCategory.Image, SizeBytes = 1, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow });
                db.Files.Add(new FileEntry { VolumeId = vol.Id, DirectoryId = root.Id, Name = "deleted.jpg", Extension = "jpg", Category = FileCategory.Image, SizeBytes = 1, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, IsIncluded = true, IsPresent = false, LastIndexedUtc = DateTime.UtcNow });
                db.Files.Add(new FileEntry { VolumeId = vol.Id, DirectoryId = root.Id, Name = "excluded.jpg", Extension = "jpg", Category = FileCategory.Image, SizeBytes = 1, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, IsIncluded = false, IsPresent = true, LastIndexedUtc = DateTime.UtcNow });
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var resp = await client.GetAsync($"/api/catalog/{volumeId}/children");
        var dto = await resp.Content.ReadFromJsonAsync<CatalogChildrenDto>(JsonOpts);

        dto!.Files.TotalCount.Should().Be(1);
        dto.Files.Items[0].Name.Should().Be("present.jpg");
    }

    [Fact]
    public async Task GetChildren_excludes_absent_directories_but_keeps_the_ones_with_an_overlay()
    {
        int volumeId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            // The seed below parks a LIVE Pending job, because the overlay it carries is only
            // valid while the job exists. With the real queue worker running, that job is
            // runnable: it executes against a volume that exists only in this database, fails,
            // and clears the very overlay this test is about — a lost race the machine wins
            // whenever it is busy. The subject here is the catalog projection, not the queue.
            DisableQueue = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume { VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Disk4", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                // Still on disk → navigable.
                db.Directories.Add(new DirectoryNode { VolumeId = vol.Id, ParentId = root.Id, Name = "Here", MaterializedPath = "Here", IsMaterialized = true, IsPresent = true });
                // Gone from disk and nothing pending on it → not navigable.
                db.Directories.Add(new DirectoryNode { VolumeId = vol.Id, ParentId = root.Id, Name = "Gone", MaterializedPath = "Gone", IsMaterialized = true, IsPresent = false });
                // Gone from disk but a queued operation still references it → must stay visible.
                // The overlay has to name a LIVE job: since step 9b the startup reconciliation
                // clears any overlay whose job is missing or already terminal, and rightly so —
                // a jobless overlay promises an operation nobody is going to run.
                var job = new OperationJob
                {
                    Type = JobType.RenameFolder, State = JobState.Pending,
                    SourceVolumeId = vol.Id, TargetVolumeId = vol.Id,
                    TargetRelativePath = "StillQueued", IsIntraVolume = true, SequenceOrder = 1,
                };
                db.OperationJobs.Add(job);
                await db.SaveChangesAsync(ct);

                db.Directories.Add(new DirectoryNode
                {
                    VolumeId = vol.Id, ParentId = root.Id, Name = "GoneButQueued", MaterializedPath = "GoneButQueued",
                    IsMaterialized = true, IsPresent = false,
                    PendingState = EntityPendingState.PendingRename, PendingJobId = job.Id,
                });
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var resp = await client.GetAsync($"/api/catalog/{volumeId}/children");
        var dto = await resp.Content.ReadFromJsonAsync<CatalogChildrenDto>(JsonOpts);

        dto!.Directories.Select(d => d.Name).Should().BeEquivalentTo(["Here", "GoneButQueued"]);
    }

    [Fact]
    public async Task GetChildren_child_count_ignores_absent_subdirectories()
    {
        int volumeId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume { VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Disk5", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                var parent = new DirectoryNode { VolumeId = vol.Id, ParentId = root.Id, Name = "Parent", MaterializedPath = "Parent", IsMaterialized = true };
                db.Directories.Add(parent);
                await db.SaveChangesAsync(ct);

                db.Directories.Add(new DirectoryNode { VolumeId = vol.Id, ParentId = parent.Id, Name = "Alive", MaterializedPath = @"Parent\Alive", IsMaterialized = true });
                db.Directories.Add(new DirectoryNode { VolumeId = vol.Id, ParentId = parent.Id, Name = "Dead", MaterializedPath = @"Parent\Dead", IsMaterialized = true, IsPresent = false });
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var resp = await client.GetAsync($"/api/catalog/{volumeId}/children");
        var dto = await resp.Content.ReadFromJsonAsync<CatalogChildrenDto>(JsonOpts);

        dto!.Directories.Single(d => d.Name == "Parent").ChildDirectoryCount.Should().Be(1);
    }

    [Fact]
    public async Task GetChildren_unknown_volume_returns_404()
    {
        using var factory = new FileTracertAppFactory { DisableVolumeSync = true, DisableScan = true };
        var client = Authed(factory);

        var resp = await client.GetAsync("/api/catalog/9999/children");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetChildren_paging_files_works()
    {
        int volumeId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume { VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "PagDisk", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                for (int i = 1; i <= 30; i++)
                {
                    db.Files.Add(new FileEntry { VolumeId = vol.Id, DirectoryId = root.Id, Name = $"img{i:D3}.jpg", Extension = "jpg", Category = FileCategory.Image, SizeBytes = i * 100, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow });
                }
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var firstPage = await (await client.GetAsync($"/api/catalog/{volumeId}/children?skip=0&take=10")).Content.ReadFromJsonAsync<CatalogChildrenDto>(JsonOpts);
        var secondPage = await (await client.GetAsync($"/api/catalog/{volumeId}/children?skip=10&take=10")).Content.ReadFromJsonAsync<CatalogChildrenDto>(JsonOpts);

        firstPage!.Files.TotalCount.Should().Be(30);
        firstPage.Files.Items.Should().HaveCount(10);
        secondPage!.Files.Items.Should().HaveCount(10);

        firstPage.Files.Items.Select(f => f.Id)
            .Intersect(secondPage.Files.Items.Select(f => f.Id))
            .Should().BeEmpty();
    }
}
