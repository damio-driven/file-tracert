using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FluentAssertions;

namespace FileTracert.Tests.Host;

/// <summary>
/// Step 9b, read side: the Catalog and the Search endpoints must serve the PROJECTION, not the
/// disk (§5). Everything goes through the real API — a real enqueue, then a real GET/POST — with
/// the queue worker off so the jobs stay pending and the projection is what is under test.
/// </summary>
public sealed class ProjectionApiTests
{
    private const string Header = "X-FileTracert-Token";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private int _alphaId;
    private int _betaId;
    private int _docsId;
    private int _subId;
    private int _reportId;

    private FileTracertAppFactory MakeFactory() => new()
    {
        DisableVolumeSync = true,
        DisableScan = true,
        // The jobs must stay queued: the projection is exactly what the user sees BEFORE
        // anything happens on disk.
        DisableQueue = true,
        Seed = async (db, ct) =>
        {
            var alpha = new Volume
            {
                VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Alpha", FileSystem = "NTFS",
                Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true, FreeBytesLastKnown = 1_000_000,
            };
            var beta = new Volume
            {
                VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Beta", FileSystem = "NTFS",
                Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true, FreeBytesLastKnown = 1_000_000,
            };
            db.Volumes.AddRange(alpha, beta);
            await db.SaveChangesAsync(ct);
            _alphaId = alpha.Id;
            _betaId = beta.Id;

            var root = new DirectoryNode { VolumeId = alpha.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
            var betaRoot = new DirectoryNode { VolumeId = beta.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
            db.Directories.AddRange(root, betaRoot);
            await db.SaveChangesAsync(ct);

            var docs = new DirectoryNode
            {
                VolumeId = alpha.Id, ParentId = root.Id, Name = "Docs",
                MaterializedPath = "Docs", IsMaterialized = true,
            };
            db.Directories.Add(docs);
            await db.SaveChangesAsync(ct);
            _docsId = docs.Id;

            var sub = new DirectoryNode
            {
                VolumeId = alpha.Id, ParentId = docs.Id, Name = "Sub",
                MaterializedPath = @"Docs\Sub", IsMaterialized = true,
            };
            db.Directories.Add(sub);
            await db.SaveChangesAsync(ct);
            _subId = sub.Id;

            var report = new FileEntry
            {
                VolumeId = alpha.Id, DirectoryId = docs.Id, Name = "report.txt", Extension = "txt",
                Category = FileCategory.Document, SizeBytes = 1024,
                FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow,
            };
            db.Files.Add(report);
            await db.SaveChangesAsync(ct);
            _reportId = report.Id;

            await new FileSearchIndex(db).SyncVolumeFromDbAsync(alpha.Id, ct);
        },
    };

    private static HttpClient Authed(FileTracertAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(Header, f.Token);
        return c;
    }

    private static async Task<OperationJobDto> EnqueueAsync(HttpClient client, CreateJobRequest req)
    {
        var resp = await client.PostAsJsonAsync("/api/operations/enqueue", req, JsonOpts);
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<OperationJobDto>(JsonOpts))!;
    }

    private static async Task<CatalogChildrenDto> ChildrenAsync(HttpClient client, int volumeId, int? directoryId)
    {
        var url = directoryId is null
            ? $"/api/catalog/{volumeId}/children"
            : $"/api/catalog/{volumeId}/children?directoryId={directoryId}";
        var resp = await client.GetAsync(url);
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<CatalogChildrenDto>(JsonOpts))!;
    }

    private static async Task<IReadOnlyList<SearchResultDto>> SearchAsync(HttpClient client, string text)
    {
        var resp = await client.PostAsJsonAsync("/api/search", new SearchRequest(
            text, SearchScope.Name, null, null, null, null, null, null, null, false,
            SearchSort.Relevance, false, 0, 20), JsonOpts);
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<PagedResult<SearchResultDto>>(JsonOpts))!.Items;
    }

    // ── Catalog ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Catalog_shows_the_projected_name_and_the_badge_of_a_queued_rename()
    {
        using var factory = MakeFactory();
        var client = Authed(factory);

        var job = await EnqueueAsync(client, new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = _reportId, NewName = "tramonto.txt",
        });

        var children = await ChildrenAsync(client, _alphaId, _docsId);
        var file = children.Files.Items.Should().ContainSingle().Subject;

        file.Name.Should().Be("tramonto.txt");
        file.ProjectedState.Should().Be(nameof(EntityPendingState.PendingRename));
        file.PendingJobId.Should().Be(job.Id);
    }

    [Fact]
    public async Task Catalog_shows_a_queued_move_at_the_destination_and_no_longer_at_the_source()
    {
        using var factory = MakeFactory();
        var client = Authed(factory);

        await EnqueueAsync(client, new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = _reportId,
            TargetVolumeId = _alphaId, TargetRelativePath = @"Docs\Sub",
        });

        var source = await ChildrenAsync(client, _alphaId, _docsId);
        source.Files.Items.Should().BeEmpty("the file is projected into its destination");
        source.Files.TotalCount.Should().Be(0);
        source.Directories.Single(d => d.Name == "Sub").FileCount
            .Should().Be(1, "the badge count and the listing must agree");

        var destination = await ChildrenAsync(client, _alphaId, _subId);
        var file = destination.Files.Items.Should().ContainSingle().Subject;
        file.Name.Should().Be("report.txt");
        file.ProjectedState.Should().Be(nameof(EntityPendingState.PendingMove));
    }

    [Fact]
    public async Task Catalog_lists_a_folder_that_is_still_only_queued_and_lets_you_open_it()
    {
        using var factory = MakeFactory();
        var client = Authed(factory);

        var job = await EnqueueAsync(client, new CreateJobRequest
        {
            Type = JobType.CreateFolder, TargetVolumeId = _alphaId,
            TargetRelativePath = @"Docs\Album 2026",
        });

        var children = await ChildrenAsync(client, _alphaId, _docsId);
        var album = children.Directories.Should().ContainSingle(d => d.Name == "Album 2026").Subject;
        album.ProjectedState.Should().Be(nameof(EntityPendingState.PendingCreate));
        album.PendingJobId.Should().Be(job.Id);

        // Navigable: the whole point of projecting it is that operations can target it.
        var inside = await ChildrenAsync(client, _alphaId, album.Id);
        inside.Files.TotalCount.Should().Be(0);
        inside.CurrentDirectoryPath.Should().Be(@"Docs\Album 2026");
    }

    [Fact]
    public async Task Catalog_shows_the_projected_name_of_a_queued_folder_rename()
    {
        using var factory = MakeFactory();
        var client = Authed(factory);

        var job = await EnqueueAsync(client, new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = _docsId, NewName = "Documenti",
        });

        var children = await ChildrenAsync(client, _alphaId, null);
        var docs = children.Directories.Should().ContainSingle().Subject;
        docs.Name.Should().Be("Documenti");
        docs.ProjectedState.Should().Be(nameof(EntityPendingState.PendingRename));
        docs.PendingJobId.Should().Be(job.Id);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_returns_the_projected_name_state_and_path()
    {
        using var factory = MakeFactory();
        var client = Authed(factory);

        await EnqueueAsync(client, new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = _reportId, NewName = "tramonto.txt",
        });

        var hit = (await SearchAsync(client, "tramonto")).Should().ContainSingle().Subject;
        hit.Name.Should().Be("tramonto.txt");
        hit.RelativePath.Should().Be(@"Docs\tramonto.txt");
        hit.ProjectedState.Should().Be(nameof(EntityPendingState.PendingRename));
    }

    [Fact]
    public async Task Search_shows_a_queued_folder_rename_in_the_result_path()
    {
        using var factory = MakeFactory();
        var client = Authed(factory);

        await EnqueueAsync(client, new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = _docsId, NewName = "Documenti",
        });

        // The FTS index is deliberately untouched by a folder rename (§5), so the file is still
        // matched by its name — but the path SHOWN is the projected one.
        var hit = (await SearchAsync(client, "report")).Should().ContainSingle().Subject;
        hit.RelativePath.Should().Be(@"Documenti\report.txt");
        hit.ProjectedState.Should().Be(nameof(EntityPendingState.None),
            "the overlay is on the folder, not on the file");
    }

    [Fact]
    public async Task Search_reports_the_destination_volume_of_a_queued_cross_volume_move()
    {
        using var factory = MakeFactory();
        var client = Authed(factory);

        await EnqueueAsync(client, new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = _reportId,
            TargetVolumeId = _betaId, TargetRelativePath = "Backup",
        });

        var hit = (await SearchAsync(client, "report")).Should().ContainSingle().Subject;
        hit.VolumeId.Should().Be(_betaId);
        hit.VolumeLabel.Should().Be("Beta");
        hit.RelativePath.Should().Be(@"Backup\report.txt");
        hit.ProjectedState.Should().Be(nameof(EntityPendingState.PendingMove));
    }
}
