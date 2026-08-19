using System.Net;
using System.Net.Http.Json;
using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Host;

/// <summary>
/// K11. "Gone" and "wrong" are two different answers, and until now the API told them apart by
/// running <c>ex.Message.Contains("not found")</c> over the exception text — so a reworded or
/// translated sentence turned a 404 into a 400 with nothing failing. These tests pin the mapping
/// on the two actions that made the distinction (retry and cancel), and they are what makes the
/// wording free to change: they assert the STATUS, never the sentence.
///
/// <para>RED before the fix was demonstrated by rewording the throw — <c>$"Job {id} is not in the
/// queue."</c> instead of "not found": both 404 cases returned 400 with the old controller, and
/// pass with the typed exception.</para>
/// </summary>
public sealed class OperationsErrorMappingTests
{
    private const string Header = "X-FileTracert-Token";

    private int _completedJobId;

    private FileTracertAppFactory MakeFactory() => new()
    {
        DisableVolumeSync = true,
        DisableScan = true,
        DisableQueue = true,
        Seed = async (db, ct) =>
        {
            var volume = new Volume
            {
                VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
                Label = "Source",
                FileSystem = "NTFS",
                Kind = VolumeKind.Fixed,
                IsCatalogable = true,
                IsOnline = true,
            };
            db.Volumes.Add(volume);
            await db.SaveChangesAsync(ct);

            // Completed is terminal: a retry of it is a legitimate request about an existing job
            // that simply cannot be honoured — the 400 half of the pair.
            var job = new OperationJob
            {
                Type = JobType.RenameFile,
                State = JobState.Completed,
                SourceVolumeId = volume.Id,
                TargetVolumeId = volume.Id,
                IsIntraVolume = true,
                SequenceOrder = 1,
                CompletedUtc = DateTime.UtcNow,
            };
            db.OperationJobs.Add(job);
            await db.SaveChangesAsync(ct);
            _completedJobId = job.Id;
        },
    };

    private static HttpClient Authed(FileTracertAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(Header, f.Token);
        return c;
    }

    [Fact]
    public async Task Retrying_a_job_that_does_not_exist_is_404()
    {
        await using var factory = MakeFactory();
        var client = Authed(factory);

        var resp = await client.PostAsync("/api/operations/999999/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancelling_a_job_that_does_not_exist_is_404()
    {
        await using var factory = MakeFactory();
        var client = Authed(factory);

        var resp = await client.DeleteAsync("/api/operations/999999");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retrying_a_completed_job_is_400_not_404()
    {
        await using var factory = MakeFactory();
        var client = Authed(factory);

        var resp = await client.PostAsync($"/api/operations/{_completedJobId}/retry", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
        body!.Error.Should().NotBeNullOrWhiteSpace("the client shows the reason it cannot retry");
    }

    [Fact]
    public async Task An_enqueue_naming_a_file_that_does_not_exist_stays_a_400()
    {
        await using var factory = MakeFactory();
        var client = Authed(factory);

        // Deliberate: what the ROUTE names missing is a 404, what the BODY names missing is a bad
        // request. That was the behaviour before the filter and it is preserved — the enqueue's
        // "File 424242 not found." never routed to 404, because only retry and cancel sniffed.
        var resp = await client.PostAsJsonAsync("/api/operations/enqueue", new
        {
            type = "RenameFile",
            sourceFileId = 424242,
            newName = "x.txt",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record ErrorBody(string Error);
}
