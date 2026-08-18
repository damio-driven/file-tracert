using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Search;
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
        INotificationPublisher? notifications = null, IScanStatusTracker? tracker = null,
        IFileSearchIndex? fts = null, int batchSize = 5_000) =>
        new(ctx, probe, usn, enumerator, meta, new BulkIndexWriter(ctx),
            new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
            fts ?? new FakeFileSearchIndex(),
            notifications ?? new FakeNotificationPublisher(),
            tracker ?? new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System), NullLogger<ScanService>.Instance)
        {
            FileBatchSize = batchSize,
        };

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

    // ── re-scan merges instead of truncating (step 9a) ────────────────────────

    /// <summary>Runs a scan over a fixed set of entries, so a test can re-scan at will.</summary>
    private static async Task ScanAsync(
        SqliteInMemoryContext harness, int volumeId, List<ScanEntry> entries,
        Func<FileTracertDbContext, IBulkIndexWriter>? writerOverride = null, int batchSize = 5_000,
        CancellationToken ct = default)
    {
        await using var ctx = harness.CreateContext();

        // The writer must be built on the SAME context the scan runs on: its raw SQLite
        // commands enlist in that context's ambient transaction.
        var bulk = writerOverride?.Invoke(ctx) ?? new BulkIndexWriter(ctx);
        var sut = new ScanService(ctx,
            new FakeVolumeProbe(ProbedFor("exFAT")),
            new FakeUsnReader([], 0),
            new FakeDirectoryEnumerator(entries),
            new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
            bulk,
            new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
            new FakeFileSearchIndex(),
            new FakeNotificationPublisher(),
            new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System),
            NullLogger<ScanService>.Instance)
        {
            FileBatchSize = batchSize,
        };
        await sut.ScanVolumeAsync(volumeId, ct);
    }

    private static List<ScanEntry> TwoFiles() =>
    [
        new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
        new(@"A\f1.dat", "f1.dat", false, 1, T, T, FileAttributes.Normal),
        new(@"A\f2.dat", "f2.dat", false, 2, T, T, FileAttributes.Normal),
    ];

    [Fact]
    public async Task Re_scan_preserves_the_pending_overlay()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);
        var entries = TwoFiles();

        await ScanAsync(harness, volumeId, entries);

        // Stand-in for what enqueue will write in 9b: the overlay lives inline on the row.
        await using (var ctx = harness.CreateContext())
        {
            var file = await ctx.Files.SingleAsync(f => f.Name == "f1.dat");
            file.PendingName = "renamed.dat";
            file.PendingState = EntityPendingState.PendingRename;
            file.PendingJobId = 7;

            var dir = await ctx.Directories.SingleAsync(d => d.MaterializedPath == "A");
            dir.PendingName = "B";
            dir.PendingState = EntityPendingState.PendingRename;
            dir.PendingJobId = 7;
            await ctx.SaveChangesAsync();
        }

        await ScanAsync(harness, volumeId, entries);

        await using var read = harness.CreateContext();
        var merged = await read.Files.SingleAsync(f => f.Name == "f1.dat");
        merged.PendingName.Should().Be("renamed.dat");
        merged.PendingState.Should().Be(EntityPendingState.PendingRename);
        merged.PendingJobId.Should().Be(7);

        var mergedDir = await read.Directories.SingleAsync(d => d.MaterializedPath == "A");
        mergedDir.PendingName.Should().Be("B");
        mergedDir.PendingState.Should().Be(EntityPendingState.PendingRename);
    }

    [Fact]
    public async Task Re_scan_preserves_row_identities()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);
        var entries = TwoFiles();

        await ScanAsync(harness, volumeId, entries);

        Dictionary<string, int> fileIds, dirIds;
        await using (var ctx = harness.CreateContext())
        {
            fileIds = await ctx.Files.ToDictionaryAsync(f => f.Name, f => f.Id);
            dirIds = await ctx.Directories.ToDictionaryAsync(d => d.MaterializedPath, d => d.Id);
        }

        await ScanAsync(harness, volumeId, entries);

        await using var read = harness.CreateContext();
        // Identities are what OperationJobItems.FileId points at: a re-scan must not renumber them.
        (await read.Files.ToDictionaryAsync(f => f.Name, f => f.Id)).Should().Equal(fileIds);
        (await read.Directories.ToDictionaryAsync(d => d.MaterializedPath, d => d.Id)).Should().Equal(dirIds);
    }

    [Fact]
    public async Task A_file_that_disappeared_is_marked_absent_and_keeps_its_row_and_overlay()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);

        await ScanAsync(harness, volumeId, TwoFiles());

        int goneId;
        await using (var ctx = harness.CreateContext())
        {
            var file = await ctx.Files.SingleAsync(f => f.Name == "f2.dat");
            goneId = file.Id;
            file.PendingState = EntityPendingState.PendingMove;
            file.PendingJobId = 11;
            await ctx.SaveChangesAsync();
        }

        // f2.dat is no longer on disk.
        await ScanAsync(harness, volumeId,
        [
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
            new(@"A\f1.dat", "f1.dat", false, 1, T, T, FileAttributes.Normal),
        ]);

        await using var read = harness.CreateContext();
        var gone = await read.Files.SingleAsync(f => f.Id == goneId);
        gone.IsPresent.Should().BeFalse();
        gone.PendingState.Should().Be(EntityPendingState.PendingMove);   // the job still references it
        gone.PendingJobId.Should().Be(11);
        (await read.Files.SingleAsync(f => f.Name == "f1.dat")).IsPresent.Should().BeTrue();
    }

    [Fact]
    public async Task A_directory_that_disappeared_is_marked_absent_instead_of_deleted()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);

        await ScanAsync(harness, volumeId,
        [
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
            new(@"B", "B", true, 0, T, T, FileAttributes.Directory),
            new(@"B\x.dat", "x.dat", false, 1, T, T, FileAttributes.Normal),
        ]);

        var bId = await ReadDirIdAsync(harness, "B");

        await ScanAsync(harness, volumeId,
        [
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
        ]);

        await using var read = harness.CreateContext();
        var b = await read.Directories.SingleAsync(d => d.Id == bId);
        b.IsPresent.Should().BeFalse();
        (await read.Directories.SingleAsync(d => d.MaterializedPath == "A")).IsPresent.Should().BeTrue();
    }

    [Fact]
    public async Task A_scan_that_revives_a_directory_still_writes_its_own_checkpoint()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);
        var entries = TwoFiles();

        await ScanAsync(harness, volumeId, entries);

        // The directory is back on disk after having been marked absent — the merge path that
        // reloads directory rows through the change tracker. That bookkeeping must not disturb
        // the volume entity the scan writes its checkpoint on.
        await using (var ctx = harness.CreateContext())
        {
            (await ctx.Directories.SingleAsync(d => d.MaterializedPath == "A")).IsPresent = false;
            (await ctx.Volumes.SingleAsync()).LastFullScanUtc = null;
            await ctx.SaveChangesAsync();
        }

        await ScanAsync(harness, volumeId, entries);

        await using var read = harness.CreateContext();
        (await read.Directories.SingleAsync(d => d.MaterializedPath == "A")).IsPresent.Should().BeTrue();
        (await read.Volumes.SingleAsync()).LastFullScanUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task A_file_that_reappears_gets_its_row_back_without_a_new_identity()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);
        var entries = TwoFiles();

        await ScanAsync(harness, volumeId, entries);
        int id;
        await using (var ctx = harness.CreateContext())
            id = await ctx.Files.Where(f => f.Name == "f2.dat").Select(f => f.Id).SingleAsync();

        await ScanAsync(harness, volumeId,
        [
            new(@"A", "A", true, 0, T, T, FileAttributes.Directory),
            new(@"A\f1.dat", "f1.dat", false, 1, T, T, FileAttributes.Normal),
        ]);
        await ScanAsync(harness, volumeId, entries);

        await using var read = harness.CreateContext();
        var back = await read.Files.SingleAsync(f => f.Name == "f2.dat");
        back.Id.Should().Be(id);
        back.IsPresent.Should().BeTrue();
        (await read.Files.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_scan_cancelled_between_batches_does_not_claim_to_be_complete()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, VolumeScanEngine.Enumeration, "exFAT", []);
        var entries = TwoFiles();

        using var cts = new CancellationTokenSource();

        // The real writer still does the work; the wrapper only trips the token after the
        // first batch has committed, which is the crash-in-the-middle case.
        var act = async () => await ScanAsync(
            harness, volumeId, entries,
            ctx => new CancelAfterFirstBatchWriter(new BulkIndexWriter(ctx), cts),
            batchSize: 1, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        await using (var read = harness.CreateContext())
        {
            var volume = await read.Volumes.SingleAsync();
            volume.LastFullScanUtc.Should().BeNull();     // never advertise a partial scan as complete
            volume.LastUsn.Should().BeNull();
            (await read.Files.CountAsync()).Should().Be(1); // the committed batch survived
        }

        // The merge is idempotent, so a plain re-scan converges — nothing to repair.
        await ScanAsync(harness, volumeId, entries);

        await using var final = harness.CreateContext();
        (await final.Files.CountAsync()).Should().Be(2);
        (await final.Files.CountAsync(f => f.IsPresent)).Should().Be(2);
        (await final.Volumes.SingleAsync()).LastFullScanUtc.Should().NotBeNull();
    }

    private static async Task<int> ReadDirIdAsync(SqliteInMemoryContext harness, string path)
    {
        await using var ctx = harness.CreateContext();
        return await ctx.Directories.Where(d => d.MaterializedPath == path).Select(d => d.Id).SingleAsync();
    }

    private sealed class CancelAfterFirstBatchWriter : IBulkIndexWriter
    {
        private readonly IBulkIndexWriter _inner;
        private readonly CancellationTokenSource _cts;
        private int _batches;

        public CancelAfterFirstBatchWriter(IBulkIndexWriter inner, CancellationTokenSource cts)
        {
            _inner = inner;
            _cts = cts;
        }

        public Task BulkInsertDirectoriesAsync(IReadOnlyCollection<DirectoryNode> nodes, CancellationToken ct) =>
            _inner.BulkInsertDirectoriesAsync(nodes, ct);

        // Both write paths are wrapped: a first scan bulk-inserts (empty catalog), a re-scan
        // merges, and either must survive being interrupted between two batches.
        public async Task BulkInsertFilesAsync(IReadOnlyCollection<FileEntry> files, CancellationToken ct)
        {
            await _inner.BulkInsertFilesAsync(files, ct);
            await CancelAfterFirstAsync();
        }

        public Task BulkUpsertFilesAsync(IReadOnlyCollection<FileEntry> files, CancellationToken ct) =>
            _inner.BulkUpsertFilesAsync(files, ct);

        public async Task<ScanMergeBatchResult> MergeScannedFilesAsync(
            int volumeId, IReadOnlyCollection<FileEntry> batch, DateTime indexedUtc, CancellationToken ct)
        {
            var result = await _inner.MergeScannedFilesAsync(volumeId, batch, indexedUtc, ct);
            await CancelAfterFirstAsync();
            return result;
        }

        private async Task CancelAfterFirstAsync()
        {
            if (++_batches == 1)
            {
                await _cts.CancelAsync();
            }
        }

        public Task<int> MarkAbsentFilesAsync(int volumeId, DateTime scanStartedUtc, CancellationToken ct) =>
            _inner.MarkAbsentFilesAsync(volumeId, scanStartedUtc, ct);
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
