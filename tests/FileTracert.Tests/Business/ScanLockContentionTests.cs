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
/// The other half of the step 9a debt: a scan used to wrap the whole volume in ONE
/// transaction, so SQLite's single write lock stayed taken for minutes and every other
/// writer (sync worker, queue, API) hit "database is locked". These tests run against a
/// real file database with one connection per context — the only setup where write-lock
/// behaviour is observable at all (the in-memory harness shares a single connection, so
/// writers there never actually contend).
/// </summary>
public sealed class ScanLockContentionTests
{
    private const string Guid = @"\\?\Volume{99999999-9999-9999-9999-999999999999}\";
    private static readonly DateTime T = new(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc);
    private const int FileCount = 60;

    [Fact]
    public async Task A_long_scan_commits_batch_by_batch_and_leaves_room_for_other_writers()
    {
        using var harness = new SqliteFileContext();
        var volumeId = await SeedAsync(harness);

        var entries = new List<ScanEntry> { new(@"A", "A", true, 0, T, T, FileAttributes.Directory) };
        for (var i = 0; i < FileCount; i++)
        {
            entries.Add(new ScanEntry($@"A\f{i:D3}.dat", $"f{i:D3}.dat", false, i + 1, T, T, FileAttributes.Normal));
        }

        // Read from an independent connection at the head of every batch. WAL readers are
        // never blocked, so this is a timing-free proof of what is already *committed*:
        // under one scan-wide transaction it would stay 0 until the very end.
        var visibleBeforeEachBatch = new List<int>();
        async Task ObserveAsync()
        {
            await using var other = harness.CreateContext();
            visibleBeforeEachBatch.Add(await other.Files.CountAsync());
        }

        using var scanning = new CancellationTokenSource();

        // Wait for the hammer to reach its loop before the scan starts. Without this the test
        // measured the thread pool as much as the lock: under a loaded full-suite run the Task.Run
        // body could first be scheduled AFTER the scan had finished, so the loop saw cancellation
        // on its very first check and returned (0 succeeded, 0 blocked) — a red that says nothing
        // about SQLite. Observed once in five full runs, always green in isolation.
        var writerRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(() => HammerWritesAsync(harness, writerRunning, scanning.Token));
        await writerRunning.Task;

        try
        {
            await using var ctx = harness.CreateContext();
            var sut = new ScanService(ctx,
                new FakeVolumeProbe(new ProbedVolume(
                    Guid, "SER", "Disk", "exFAT", IsRemovable: false,
                    MountPoints: [@"X:\"], CapacityBytes: 5000, FreeBytes: 2000, PhysicalDiskId: null)),
                new FakeUsnReader([], 0),
                new FakeDirectoryEnumerator(entries),
                new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
                new ProbingBulkIndexWriter(new BulkIndexWriter(ctx), ObserveAsync),
                new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
                new FakeFileSearchIndex(),
                new FakeNotificationPublisher(),
                new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System),
                NullLogger<ScanService>.Instance)
            {
                FileBatchSize = 1,
            };

            await sut.ScanVolumeAsync(volumeId, CancellationToken.None);
        }
        finally
        {
            // Even if the scan throws, the background writer has to be told to stop: an
            // unbounded retry loop against a torn-down temp database is how a test host ends
            // up hung rather than failed.
            await scanning.CancelAsync();
        }

        var (succeeded, blocked) = await writer;

        visibleBeforeEachBatch.Should().HaveCount(FileCount);
        visibleBeforeEachBatch[0].Should().Be(0);
        visibleBeforeEachBatch.Should().BeInAscendingOrder();
        visibleBeforeEachBatch[^1].Should().Be(FileCount - 1,
            "every batch but the last must already be committed and visible to another connection");

        succeeded.Should().BeGreaterThan(0,
            $"another writer must get the write lock while a scan runs (blocked attempts: {blocked})");

        await using var read = harness.CreateContext();
        (await read.Files.CountAsync()).Should().Be(FileCount);
    }

    /// <summary>
    /// Keeps writing small rows from its own connection for as long as the scan runs, with the
    /// harness's short busy timeout: an attempt that cannot get the write lock in time fails
    /// rather than waiting the scan out, which is exactly the SQLITE_BUSY the old monolithic
    /// transaction produced.
    /// </summary>
    private static async Task<(int Succeeded, int Blocked)> HammerWritesAsync(
        SqliteFileContext harness, TaskCompletionSource running, CancellationToken ct)
    {
        var succeeded = 0;
        var blocked = 0;

        // Signalled before the first attempt, so the caller knows the loop exists rather than
        // hoping the thread pool got to it in time.
        running.SetResult();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var db = harness.CreateContext();
                db.Notifications.Add(new Notification
                {
                    TimestampUtc = DateTime.UtcNow,
                    Severity = NotificationSeverity.Info,
                    Source = "contention-probe",
                    Title = "written while the scan was running",
                    Message = "probe",
                });
                await db.SaveChangesAsync(CancellationToken.None);
                succeeded++;
            }
            catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
            {
                blocked++;
            }

            await Task.Delay(5, CancellationToken.None);
        }

        return (succeeded, blocked);
    }

    private static async Task<int> SeedAsync(SqliteFileContext harness)
    {
        await using var ctx = harness.CreateContext();
        ctx.AppSettings.Add(new AppSettings
        {
            DefaultExtensionFilter = [], ExcludedPaths = [], ApiToken = "token", SpaceMarginPercent = 5,
        });

        var volume = new Volume
        {
            VolumeGuid = Guid, FileSystem = "exFAT", ScanEngine = VolumeScanEngine.Enumeration, IsOnline = true,
        };
        ctx.Volumes.Add(volume);
        await ctx.SaveChangesAsync();

        ctx.WatchedRoots.Add(new WatchedRoot { VolumeId = volume.Id, RelativePath = "", IsActive = true });
        await ctx.SaveChangesAsync();
        return volume.Id;
    }

    /// <summary>Delegates everything to the real writer, observing once per file batch.</summary>
    private sealed class ProbingBulkIndexWriter : IBulkIndexWriter
    {
        private readonly IBulkIndexWriter _inner;
        private readonly Func<Task> _observe;

        public ProbingBulkIndexWriter(IBulkIndexWriter inner, Func<Task> observe)
        {
            _inner = inner;
            _observe = observe;
        }

        public Task BulkInsertDirectoriesAsync(IReadOnlyCollection<DirectoryNode> nodes, CancellationToken ct) =>
            _inner.BulkInsertDirectoriesAsync(nodes, ct);

        // Both write paths are observed: a first scan bulk-inserts (empty catalog), a re-scan
        // merges, and the batch-per-transaction guarantee has to hold for either.
        public async Task BulkInsertFilesAsync(IReadOnlyCollection<FileEntry> files, CancellationToken ct)
        {
            await _observe();
            await _inner.BulkInsertFilesAsync(files, ct);
        }

        public Task BulkUpsertFilesAsync(IReadOnlyCollection<FileEntry> files, CancellationToken ct) =>
            _inner.BulkUpsertFilesAsync(files, ct);

        public async Task<ScanMergeBatchResult> MergeScannedFilesAsync(
            int volumeId, IReadOnlyCollection<FileEntry> batch, DateTime indexedUtc, CancellationToken ct)
        {
            await _observe();
            return await _inner.MergeScannedFilesAsync(volumeId, batch, indexedUtc, ct);
        }

        public Task<ScanClosureResult> ReconcileUnseenFilesAsync(
            int volumeId, DateTime scanStartedUtc, IReadOnlyCollection<SkippedScanArea> skipped, CancellationToken ct) =>
            _inner.ReconcileUnseenFilesAsync(volumeId, scanStartedUtc, skipped, ct);
    }
}
