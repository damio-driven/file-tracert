using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Paging;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Host;

/// <summary>
/// C25 over the real API: the picker sends ONE request for a whole selection.
/// The queue worker is off — what is under test is what the enqueue leaves behind, not
/// what the engine then does with it.
/// </summary>
public sealed class BatchEnqueueApiTests
{
    private const string Header = "X-FileTracert-Token";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private int _sourceVolumeId;
    private int _targetVolumeId;
    private int _fileAId;
    private int _fileBId;

    private FileTracertAppFactory MakeFactory() => new()
    {
        DisableVolumeSync = true,
        DisableScan = true,
        DisableQueue = true,
        Seed = async (db, ct) =>
        {
            var source = new Volume
            {
                VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Source", FileSystem = "NTFS",
                Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true, FreeBytesLastKnown = 1_000_000,
            };
            var target = new Volume
            {
                VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Target", FileSystem = "NTFS",
                Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true, FreeBytesLastKnown = 1_000_000,
            };
            db.Volumes.AddRange(source, target);
            await db.SaveChangesAsync(ct);
            _sourceVolumeId = source.Id;
            _targetVolumeId = target.Id;

            var root = new DirectoryNode
            {
                VolumeId = source.Id, Name = "", MaterializedPath = "", IsMaterialized = true,
            };
            var targetRoot = new DirectoryNode
            {
                VolumeId = target.Id, Name = "", MaterializedPath = "", IsMaterialized = true,
            };
            db.Directories.AddRange(root, targetRoot);
            await db.SaveChangesAsync(ct);

            var docs = new DirectoryNode
            {
                VolumeId = source.Id, ParentId = root.Id, Name = "Docs",
                MaterializedPath = "Docs", IsMaterialized = true,
            };
            db.Directories.Add(docs);
            await db.SaveChangesAsync(ct);

            var a = NewFile(source.Id, docs.Id, "a.bin");
            var b = NewFile(source.Id, docs.Id, "b.bin");
            db.Files.AddRange(a, b);
            await db.SaveChangesAsync(ct);
            _fileAId = a.Id;
            _fileBId = b.Id;
        },
    };

    private static FileEntry NewFile(int volumeId, int directoryId, string name) => new()
    {
        VolumeId = volumeId, DirectoryId = directoryId, Name = name, Extension = "bin",
        Category = FileCategory.Other, SizeBytes = 1_024,
        FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
        IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow,
    };

    private static HttpClient Authed(FileTracertAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(Header, f.Token);
        return c;
    }

    private CreateJobRequest Move(int fileId) => new()
    {
        Type = JobType.MoveFile,
        SourceFileId = fileId,
        TargetVolumeId = _targetVolumeId,
        TargetRelativePath = "Archivio",
    };

    private static async Task<int> QueuedCountAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/operations?skip=0&take=50");
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<PagedResult<OperationJobDto>>(JsonOpts))!.TotalCount;
    }

    [Fact]
    public async Task One_call_enqueues_the_whole_selection()
    {
        using var factory = MakeFactory();
        var client = Authed(factory);

        var resp = await client.PostAsJsonAsync(
            "/api/operations/enqueue-batch", new[] { Move(_fileAId), Move(_fileBId) }, JsonOpts);

        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var jobs = (await resp.Content.ReadFromJsonAsync<List<OperationJobDto>>(JsonOpts))!;

        jobs.Should().HaveCount(2);
        jobs.Select(j => j.Id).Should().OnlyHaveUniqueItems();
        jobs.Select(j => j.SequenceOrder).Should().OnlyHaveUniqueItems();
        jobs.Should().OnlyContain(j => j.State == "Pending");
        (await QueuedCountAsync(client)).Should().Be(2);
    }

    [Fact]
    public async Task A_selection_with_one_bad_item_enqueues_nothing_and_says_which_one()
    {
        using var factory = MakeFactory();
        var client = Authed(factory);

        var resp = await client.PostAsJsonAsync(
            "/api/operations/enqueue-batch",
            new[] { Move(_fileAId), Move(fileId: 999_999), Move(_fileBId) },
            JsonOpts);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>(JsonOpts);
        body!.Error.Should().Contain("Elemento 2 di 3");
        body.Error.Should().Contain("Nessuna operazione è stata accodata");

        // The queue is exactly as it was: the same click, corrected, cannot duplicate anything.
        (await QueuedCountAsync(client)).Should().Be(0);
    }

    private sealed record ErrorBody(string Error);
}
