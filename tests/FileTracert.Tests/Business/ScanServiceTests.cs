using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

public sealed class ScanServiceTests
{
    private const string Guid = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\";
    private static readonly DateTime T = new(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);

    private static ProbedVolume ProbedFor(string fileSystem) => new(
        Guid, "SER-1", "Disk", fileSystem, IsRemovable: false,
        MountPoints: [@"X:\"], CapacityBytes: 5000, FreeBytes: 2000, PhysicalDiskId: null);

    private static int Seed(SqliteInMemoryContext harness, VolumeScanEngine engine, string fileSystem, List<string> extensions)
    {
        using var ctx = harness.CreateContext();
        ctx.AppSettings.RemoveRange(ctx.AppSettings);
        ctx.AppSettings.Add(new AppSettings
        {
            DefaultExtensionFilter = extensions,
            ExcludedPaths = [],
            ApiToken = "token",
            SpaceMarginPercent = 5,
        });

        var volume = new Volume
        {
            VolumeGuid = Guid,
            FileSystem = fileSystem,
            ScanEngine = engine,
            IsOnline = true,
        };
        ctx.Volumes.Add(volume);
        ctx.SaveChanges();

        ctx.WatchedRoots.Add(new WatchedRoot { VolumeId = volume.Id, RelativePath = "", IsActive = true });
        ctx.SaveChanges();
        return volume.Id;
    }

    private static ScanService Build(SqliteInMemoryContext harness, FileTracertDbContext ctx,
        IVolumeProbe probe, IUsnReader usn, IDirectoryEnumerator enumerator, IFileMetadataReader meta) =>
        new(ctx, probe, usn, enumerator, meta, new BulkIndexWriter(ctx), NullLogger<ScanService>.Instance);

    [Fact]
    public async Task Full_scan_via_enumeration_builds_tree_applies_filters_and_sizes()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", ["jpg", "cr2"]);

        var entries = new List<ScanEntry>
        {
            new(@"Photos", "Photos", true, 0, T, T, FileAttributes.Directory),
            new(@"Photos\a.jpg", "a.jpg", false, 10, T, T, FileAttributes.Normal),
            new(@"Photos\b.txt", "b.txt", false, 20, T, T, FileAttributes.Normal), // filtered out
            new(@"Photos\Raw", "Raw", true, 0, T, T, FileAttributes.Directory),
            new(@"Photos\Raw\c.cr2", "c.cr2", false, 30, T, T, FileAttributes.Normal),
        };

        await using (var ctx = harness.CreateContext())
        {
            var sut = Build(harness, ctx,
                new FakeVolumeProbe(ProbedFor("exFAT")),
                new FakeUsnReader([], 0),
                new FakeDirectoryEnumerator(entries),
                new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()));
            await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var files = await read.Files.Include(f => f.Directory).ToListAsync();
        var dirs = await read.Directories.ToListAsync();

        files.Select(f => f.Name).Should().BeEquivalentTo("a.jpg", "c.cr2");
        dirs.Select(d => d.MaterializedPath).Should().BeEquivalentTo("", "Photos", @"Photos\Raw");

        var jpg = files.Single(f => f.Name == "a.jpg");
        jpg.Directory.MaterializedPath.Should().Be("Photos");
        jpg.Category.Should().Be(FileCategory.Image);
        jpg.SizeBytes.Should().Be(10);
        files.Single(f => f.Name == "c.cr2").SizeBytes.Should().Be(30);

        var volume = await read.Volumes.SingleAsync();
        volume.LastFullScanUtc.Should().NotBeNull();
        volume.LastUsn.Should().BeNull();
    }

    [Fact]
    public async Task Full_scan_via_usn_fills_sizes_and_checkpoints_journal()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.UsnJournal, "NTFS", ["pdf"]);

        var usnEntries = new List<UsnEntry>
        {
            new(100, 5, "Docs", @"Docs", true, null, FileAttributes.Directory, 7),
            new(101, 100, "r.pdf", @"Docs\r.pdf", false, null, FileAttributes.Normal, 8),
            new(102, 100, "skip.txt", @"Docs\skip.txt", false, null, FileAttributes.Normal, 9),
        };
        var meta = new Dictionary<string, FileMetadata>
        {
            [@"Docs\r.pdf"] = new FileMetadata(1234, T, T),
        };

        await using (var ctx = harness.CreateContext())
        {
            var sut = Build(harness, ctx,
                new FakeVolumeProbe(ProbedFor("NTFS")),
                new FakeUsnReader(usnEntries, nextUsn: 12345),
                new FakeDirectoryEnumerator([]),
                new FakeFileMetadataReader(meta));
            await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var files = await read.Files.ToListAsync();
        var docs = await read.Directories.SingleAsync(d => d.MaterializedPath == "Docs");

        files.Should().ContainSingle();
        var pdf = files.Single();
        pdf.Name.Should().Be("r.pdf");
        pdf.SizeBytes.Should().Be(1234);
        pdf.UsnFileRef.Should().Be(101);
        docs.UsnFileRef.Should().Be(100);

        var volume = await read.Volumes.SingleAsync();
        volume.LastUsn.Should().Be(12345);
    }

    [Fact]
    public async Task Ntfs_without_journal_falls_back_to_enumeration_and_persists_engine()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.UsnJournal, "NTFS", ["jpg"]);

        var entries = new List<ScanEntry>
        {
            new(@"Pics", "Pics", true, 0, T, T, FileAttributes.Directory),
            new(@"Pics\x.jpg", "x.jpg", false, 5, T, T, FileAttributes.Normal),
        };

        await using (var ctx = harness.CreateContext())
        {
            var sut = Build(harness, ctx,
                new FakeVolumeProbe(ProbedFor("NTFS")),
                new ThrowingUsnReader(), // EnsureJournal throws Win32 1179
                new FakeDirectoryEnumerator(entries),
                new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()));
            await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var volume = await read.Volumes.SingleAsync();

        // Engine flipped to Enumeration and persisted, so future cycles don't retry USN.
        volume.ScanEngine.Should().Be(VolumeScanEngine.Enumeration);
        volume.LastFullScanUtc.Should().NotBeNull();
        volume.LastUsn.Should().BeNull();

        // The volume still got indexed via the enumeration fallback.
        (await read.Files.Select(f => f.Name).ToListAsync()).Should().Equal("x.jpg");
    }

    [Fact]
    public async Task Re_scan_is_idempotent()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);

        var entries = new List<ScanEntry>
        {
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
            new(@"A\f1.dat", "f1.dat", false, 1, T, T, FileAttributes.Normal),
            new(@"A\f2.dat", "f2.dat", false, 2, T, T, FileAttributes.Normal),
        };

        async Task ScanOnce()
        {
            await using var ctx = harness.CreateContext();
            var sut = Build(harness, ctx,
                new FakeVolumeProbe(ProbedFor("exFAT")),
                new FakeUsnReader([], 0),
                new FakeDirectoryEnumerator(entries),
                new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()));
            await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
        }

        await ScanOnce();
        await ScanOnce();

        await using var read = harness.CreateContext();
        (await read.Files.CountAsync()).Should().Be(2);
        (await read.Directories.CountAsync()).Should().Be(2); // root + A
    }
}
