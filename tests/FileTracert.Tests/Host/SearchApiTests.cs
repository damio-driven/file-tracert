using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FluentAssertions;

namespace FileTracert.Tests.Host;

public sealed class SearchApiTests
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

    private static FileTracertAppFactory MakeFactory(
        IEnumerable<(string Name, string Ext, FileCategory Cat)> files)
    {
        var guid = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        return new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume
                {
                    VolumeGuid = guid, Label = "TestDisk", FileSystem = "NTFS",
                    Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true,
                };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);

                var root = new DirectoryNode
                {
                    VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true,
                };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                foreach (var (name, ext, cat) in files)
                {
                    db.Files.Add(new FileEntry
                    {
                        VolumeId = vol.Id, DirectoryId = root.Id,
                        Name = name, Extension = ext, Category = cat,
                        SizeBytes = 1024, FileCreatedUtc = DateTime.UtcNow,
                        FileModifiedUtc = DateTime.UtcNow,
                        IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow,
                    });
                }
                await db.SaveChangesAsync(ct);

                // Populate FTS5 so the search endpoint has data to find.
                await new FileSearchIndex(db).SyncVolumeFromDbAsync(vol.Id, ct);
            },
        };
    }

    [Fact]
    public async Task Post_search_finds_file_by_name()
    {
        using var factory = MakeFactory([("holiday.jpg", "jpg", FileCategory.Image)]);
        var client = Authed(factory);

        var resp = await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "holiday", SearchScope.Name, null, null, null, null, null, null, null, false,
            SearchSort.Relevance, false, 0, 10));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await resp.Content.ReadFromJsonAsync<PagedResult<SearchResultDto>>(JsonOpts);
        page!.TotalCount.Should().Be(1);
        page.Items[0].Name.Should().Be("holiday.jpg");
    }

    [Fact]
    public async Task Post_search_category_filter_excludes_non_matching()
    {
        using var factory = MakeFactory([
            ("photo.jpg", "jpg", FileCategory.Image),
            ("movie.mp4", "mp4", FileCategory.Video),
        ]);
        var client = Authed(factory);

        var resp = await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "photo", SearchScope.Name, FileCategory.Image, null, null, null, null, null, null, false,
            SearchSort.Relevance, false, 0, 10));

        var page = await resp.Content.ReadFromJsonAsync<PagedResult<SearchResultDto>>(JsonOpts);
        page!.TotalCount.Should().Be(1);
        page.Items.Should().OnlyContain(r => r.Category == FileCategory.Image);
    }

    [Fact]
    public async Task Post_search_paging_works()
    {
        var files = Enumerable.Range(1, 25)
            .Select(i => ($"file{i:D2}.jpg", "jpg", FileCategory.Image))
            .ToArray();
        using var factory = MakeFactory(files);
        var client = Authed(factory);

        var firstPage = await (await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "file", SearchScope.Name, null, null, null, null, null, null, null, false,
            SearchSort.Name, false, 0, 10))).Content.ReadFromJsonAsync<PagedResult<SearchResultDto>>(JsonOpts);

        firstPage!.TotalCount.Should().Be(25);
        firstPage.Items.Should().HaveCount(10);

        var secondPage = await (await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "file", SearchScope.Name, null, null, null, null, null, null, null, false,
            SearchSort.Name, false, 10, 10))).Content.ReadFromJsonAsync<PagedResult<SearchResultDto>>(JsonOpts);

        secondPage!.Items.Should().HaveCount(10);
        firstPage.Items.Select(r => r.FileId)
            .Intersect(secondPage.Items.Select(r => r.FileId))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Post_search_returns_bad_request_for_empty_text()
    {
        using var factory = new FileTracertAppFactory { DisableVolumeSync = true, DisableScan = true };
        var client = Authed(factory);

        var resp = await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "", SearchScope.Name, null, null, null, null, null, null, null, false,
            SearchSort.Relevance, false, 0, 10));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
