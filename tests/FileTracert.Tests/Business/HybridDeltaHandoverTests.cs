using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// The handover A4 exists for: a volume whose first scan walked its watched roots by enumeration
/// must still be servable by the incremental path. This is the case that used to be refused
/// outright — and, if the refusal had simply been removed, would have silently indexed nothing.
/// </summary>
public sealed class HybridDeltaHandoverTests
{
    private const string Guid = @"\\?\Volume{77777777-7777-7777-7777-777777777777}\";
    private static readonly DateTime T = new(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);

    private static ProbedVolume Probed => new(
        Guid, "SER-7", "Disk", "NTFS", IsRemovable: false,
        MountPoints: [@"X:\"], CapacityBytes: 5000, FreeBytes: 2000, PhysicalDiskId: null);

    [Fact]
    public async Task A_delta_places_records_against_rows_an_enumeration_walk_wrote()
    {
        using var harness = new SqliteInMemoryContext();

        int volumeId;
        await using (var ctx = harness.CreateContext())
        {
            ctx.AppSettings.RemoveRange(ctx.AppSettings);
            ctx.AppSettings.Add(new AppSettings
            {
                DefaultExtensionFilter = ["jpg"],
                ExcludedPaths = [],
                ApiToken = "token",
                SpaceMarginPercent = 5,
            });

            var volume = new Volume
            {
                VolumeGuid = Guid,
                FileSystem = "NTFS",
                ScanEngine = VolumeScanEngine.Enumeration,
                IsOnline = true,
            };
            ctx.Volumes.Add(volume);
            await ctx.SaveChangesAsync();
            volumeId = volume.Id;

            ctx.WatchedRoots.Add(new WatchedRoot { VolumeId = volumeId, RelativePath = "Photos", IsActive = true });
            await ctx.SaveChangesAsync();
        }

        var enumerator = new FakeDirectoryEnumerator(
        [
            new(@"Photos\Raw", "Raw", true, 0, T, T, FileAttributes.Directory, 110UL),
            new(@"Photos\a.jpg", "a.jpg", false, 10, T, T, FileAttributes.Normal, 200UL),
        ])
        {
            FileIdsByPath = new Dictionary<string, ulong> { ["Photos"] = 100UL },
        };

        var reader = new ScriptedUsnReader { JournalId = 7 };
        reader.Script([], nextUsn: 500);

        await using (var ctx = harness.CreateContext())
        {
            var scan = new ScanService(
                ctx,
                new FakeVolumeProbe(Probed),
                reader,
                enumerator,
                new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
                new BulkIndexWriter(ctx),
                new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
                new FakeFileSearchIndex(),
                new FakeNotificationPublisher(),
                new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System),
                NullLogger<ScanService>.Instance);

            await scan.ScanVolumeAsync(volumeId, CancellationToken.None);
        }

        // One file created inside a directory the enumeration walk indexed, and one inside the
        // watched root itself — the row no walk ever handed over.
        reader.Script(
        [
            Change(900, parent: 110, "new.jpg", @"Photos\Raw\new.jpg", usn: 600),
            Change(901, parent: 100, "root.jpg", @"Photos\root.jpg", usn: 601),
        ], nextUsn: 700);

        await using (var ctx = harness.CreateContext())
        {
            var applier = new UsnDeltaApplier(
                ctx,
                new FakeVolumeProbe(Probed),
                reader,
                new FakeFileMetadataReader(new Dictionary<string, FileMetadata>
                {
                    [@"Photos\Raw\new.jpg"] = new(12, T, T),
                    [@"Photos\root.jpg"] = new(13, T, T),
                }),
                new BulkIndexWriter(ctx),
                new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
                new FakeFileSearchIndex(),
                NullLogger<UsnDeltaApplier>.Instance);

            var result = await applier.SyncVolumeAsync(volumeId, CancellationToken.None);

            result.Status.Should().Be(UsnSyncStatus.Applied);
            result.Unresolved.Should().Be(0, "every parent has a row that carries its identity");
            result.Indexed.Should().Be(2);
        }

        await using var read = harness.CreateContext();
        var names = await read.Files.Where(f => f.IsIncluded).Select(f => f.Name).ToListAsync();
        names.Should().BeEquivalentTo("a.jpg", "new.jpg", "root.jpg");
    }

    private static UsnChangeRecord Change(
        ulong frn, ulong parent, string name, string relativePath, long usn) =>
        new(
            new UsnEntry(frn, parent, name, relativePath, IsDirectory: false, SizeBytes: null,
                Attributes: FileAttributes.Normal, Usn: usn),
            UsnReason.FileCreate,
            IsRename: false,
            OldName: null);
}
