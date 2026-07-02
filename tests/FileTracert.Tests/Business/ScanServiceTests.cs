using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Scanning;
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
        IVolumeProbe probe, IUsnReader usn, IDirectoryEnumerator enumerator, IFileMetadataReader meta,
        INotificationPublisher? notifications = null, IScanStatusTracker? tracker = null) =>
        new(ctx, probe, usn, enumerator, meta, new BulkIndexWriter(ctx),
            new FakeFileSearchIndex(),
            notifications ?? new FakeNotificationPublisher(),
            tracker ?? new ScanStatusTracker(), NullLogger<ScanService>.Instance);

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
    public async Task Ntfs_without_journal_falls_back_to_enumeration_for_this_scan()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.UsnJournal, "NTFS", ["jpg"]);

        var entries = new List<ScanEntry>
        {
            new(@"Pics", "Pics", true, 0, T, T, FileAttributes.Directory),
            new(@"Pics\x.jpg", "x.jpg", false, 5, T, T, FileAttributes.Normal),
        };

        var notifications = new FakeNotificationPublisher();
        await using (var ctx = harness.CreateContext())
        {
            var sut = Build(harness, ctx,
                new FakeVolumeProbe(ProbedFor("NTFS")),
                new ThrowingUsnReader(), // EnsureJournal throws Win32 1179
                new FakeDirectoryEnumerator(entries),
                new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
                notifications);
            await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var volume = await read.Volumes.SingleAsync();

        // Engine recorded as Enumeration for THIS scan (what was actually used) — but
        // this is not a one-way trap: the next scan re-attempts USN regardless of this
        // value (see Ntfs_stuck_on_enumeration_from_a_past_failure_recovers_to_usn_once_journal_works).
        volume.ScanEngine.Should().Be(VolumeScanEngine.Enumeration);
        volume.LastFullScanUtc.Should().NotBeNull();
        volume.LastUsn.Should().BeNull();

        // The volume still got indexed via the enumeration fallback.
        (await read.Files.Select(f => f.Name).ToListAsync()).Should().Equal("x.jpg");

        // Resilience, not silence: the degraded path raised a user-visible notification.
        notifications.Published.Should().ContainSingle()
            .Which.Severity.Should().Be(NotificationSeverity.Warning);
    }

    [Fact]
    public async Task Ntfs_stuck_on_enumeration_from_a_past_failure_recovers_to_usn_once_journal_works()
    {
        using var harness = new SqliteInMemoryContext();
        // Simulates the exact state a prior USN failure leaves behind (e.g. the service
        // wasn't elevated on an earlier run): NTFS volume, but ScanEngine persisted as
        // Enumeration. The engine choice must be re-derived from the filesystem every
        // scan, not trusted from this stale value, or it would never recover even after
        // the real problem (elevation) is fixed.
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "NTFS", ["pdf"]);

        var usnEntries = new List<UsnEntry>
        {
            new(100, 5, "Docs", @"Docs", true, null, FileAttributes.Directory, 7),
            new(101, 100, "r.pdf", @"Docs\r.pdf", false, null, FileAttributes.Normal, 8),
        };
        var meta = new Dictionary<string, FileMetadata> { [@"Docs\r.pdf"] = new FileMetadata(1234, T, T) };

        await using (var ctx = harness.CreateContext())
        {
            var sut = Build(harness, ctx,
                new FakeVolumeProbe(ProbedFor("NTFS")),
                new FakeUsnReader(usnEntries, nextUsn: 999), // journal works now
                new FakeDirectoryEnumerator([]),
                new FakeFileMetadataReader(meta));
            await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var volume = await read.Volumes.SingleAsync();

        volume.ScanEngine.Should().Be(VolumeScanEngine.UsnJournal);
        volume.LastUsn.Should().Be(999);
        (await read.Files.Select(f => f.Name).ToListAsync()).Should().Equal("r.pdf");
    }

    [Fact]
    public async Task Malformed_filter_override_notifies_and_falls_back_to_default()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []); // empty default = all types

        // Corrupt the root's override JSON.
        using (var ctx = harness.CreateContext())
        {
            var root = ctx.WatchedRoots.Single();
            root.FilterOverrideJson = "{ not valid json";
            ctx.SaveChanges();
        }

        var entries = new List<ScanEntry>
        {
            new(@"Docs", "Docs", true, 0, T, T, FileAttributes.Directory),
            new(@"Docs\a.txt", "a.txt", false, 4, T, T, FileAttributes.Normal),
        };

        var notifications = new FakeNotificationPublisher();
        await using (var ctx = harness.CreateContext())
        {
            var sut = Build(harness, ctx,
                new FakeVolumeProbe(ProbedFor("exFAT")),
                new FakeUsnReader([], 0),
                new FakeDirectoryEnumerator(entries),
                new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
                notifications);
            await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        // Scan proceeded with the default (empty = all) filter.
        (await read.Files.Select(f => f.Name).ToListAsync()).Should().Equal("a.txt");
        (await read.Volumes.SingleAsync()).LastFullScanUtc.Should().NotBeNull();

        // Not silent: the malformed override raised a user-visible warning.
        notifications.Published.Should().ContainSingle()
            .Which.Severity.Should().Be(NotificationSeverity.Warning);
    }

    [Fact]
    public async Task Scan_drives_the_status_tracker_through_phases_then_completes()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);

        var entries = new List<ScanEntry>
        {
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
            new(@"A\f.dat", "f.dat", false, 1, T, T, FileAttributes.Normal),
        };

        var tracker = new RecordingScanStatusTracker();
        await using (var ctx = harness.CreateContext())
        {
            var sut = Build(harness, ctx,
                new FakeVolumeProbe(ProbedFor("exFAT")),
                new FakeUsnReader([], 0),
                new FakeDirectoryEnumerator(entries),
                new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
                tracker: tracker);
            await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
        }

        tracker.Begun.Should().Equal(volumeId);
        tracker.Phases.Should().ContainInOrder(ScanPhase.ReadingMetadata, ScanPhase.Writing);
        tracker.LastWritten.Should().Be(1);
        tracker.Completed.Should().Equal(volumeId);
        tracker.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_failure_marks_the_tracker_failed_and_rethrows()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);

        var tracker = new RecordingScanStatusTracker();
        await using var ctx = harness.CreateContext();
        var sut = Build(harness, ctx,
            new FakeVolumeProbe(ProbedFor("exFAT")),
            new FakeUsnReader([], 0),
            new ThrowingDirectoryEnumerator(),
            new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
            tracker: tracker);

        var act = async () => await sut.ScanVolumeAsync(volumeId, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        tracker.Begun.Should().Equal(volumeId);
        tracker.Failed.Should().Equal(volumeId);
        tracker.Completed.Should().BeEmpty();
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
