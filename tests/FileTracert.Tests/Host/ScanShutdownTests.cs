using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using FileTracert.Tests.Business;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Host;

/// <summary>
/// Mirrors the graceful-shutdown guarantee the host relies on: when the token is
/// cancelled mid-scan, <see cref="ScanService"/> never commits a half-built index.
/// </summary>
public sealed class ScanShutdownTests
{
    private const string Guid = @"\\?\Volume{44444444-4444-4444-4444-444444444444}\";
    private static readonly DateTime T = new(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Cancellation_during_scan_leaves_no_partial_index()
    {
        using var harness = new SqliteInMemoryContext();
        int volumeId;
        using (var ctx = harness.CreateContext())
        {
            ctx.AppSettings.Add(new AppSettings
            {
                DefaultExtensionFilter = ["pdf"],
                ExcludedPaths = [],
                ApiToken = "t",
                SpaceMarginPercent = 3,
            });
            var volume = new Volume
            {
                VolumeGuid = Guid,
                FileSystem = "NTFS",
                ScanEngine = VolumeScanEngine.UsnJournal,
                IsOnline = true,
            };
            ctx.Volumes.Add(volume);
            ctx.SaveChanges();
            ctx.WatchedRoots.Add(new WatchedRoot { VolumeId = volume.Id, RelativePath = "", IsActive = true });
            ctx.SaveChanges();
            volumeId = volume.Id;
        }

        var usnEntries = new List<UsnEntry>
        {
            new(100, 5, "Docs", @"Docs", true, null, FileAttributes.Directory, 7),
            new(101, 100, "r.pdf", @"Docs\r.pdf", false, null, FileAttributes.Normal, 8),
        };

        using var cts = new CancellationTokenSource();
        var probe = new FakeVolumeProbe(new ProbedVolume(
            Guid, "SER", "Disk", "NTFS", IsRemovable: false,
            MountPoints: [@"X:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null));

        await using (var ctx = harness.CreateContext())
        {
            var sut = new ScanService(
                ctx,
                probe,
                new FakeUsnReader(usnEntries, nextUsn: 999),
                new FakeDirectoryEnumerator([]),
                // Cancels the token mid-scan, just before persistence.
                new CancellingMetadataReader(cts, new Dictionary<string, FileMetadata>
                {
                    [@"Docs\r.pdf"] = new FileMetadata(1234, T, T),
                }),
                new BulkIndexWriter(ctx),
                new FakeFileSearchIndex(),
                new FakeNotificationPublisher(),
                new ScanStatusTracker(),
                NullLogger<ScanService>.Instance);

            var act = async () => await sut.ScanVolumeAsync(volumeId, cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        await using var read = harness.CreateContext();
        (await read.Files.CountAsync()).Should().Be(0);
        (await read.Directories.CountAsync()).Should().Be(0);
        (await read.Volumes.SingleAsync()).LastFullScanUtc.Should().BeNull();
    }

    private sealed class CancellingMetadataReader(
        CancellationTokenSource cts,
        IReadOnlyDictionary<string, FileMetadata> map) : IFileMetadataReader
    {
        public Task<IReadOnlyDictionary<string, FileMetadata>> ReadAsync(
            string mountRoot,
            IReadOnlyCollection<string> relativePaths,
            CancellationToken ct)
        {
            cts.Cancel();
            return Task.FromResult(map);
        }
    }
}
