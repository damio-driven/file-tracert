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
                new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
                new FakeFileSearchIndex(),
                new FakeNotificationPublisher(),
                new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System),
                NullLogger<ScanService>.Instance);

            var act = async () => await sut.ScanVolumeAsync(volumeId, cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        await using var read = harness.CreateContext();
        (await read.Files.CountAsync()).Should().Be(0);
        (await read.Directories.CountAsync()).Should().Be(0);
        (await read.Volumes.SingleAsync()).LastFullScanUtc.Should().BeNull();
    }

    /// <summary>
    /// C18: the enumeration phase is the long one (minutes on a real volume). A stop that
    /// arrives while it is running must get out of it, not wait for the walk to end — the
    /// service otherwise blows past <c>ShutdownTimeout</c> and is killed.
    /// </summary>
    [Fact]
    public async Task Cancellation_during_enumeration_exits_within_the_shutdown_budget()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = SeedVolume(harness, VolumeScanEngine.Enumeration, "exFAT");

        // The fake deliberately ignores the token: what is under test is that the scan
        // itself stops consuming, not that a well-behaved port stops producing.
        // 1 000 items × a sleep the OS rounds up to ~15 ms ≈ 15 s of walking: three times the
        // budget, so an implementation that ignores the token cannot pass by being fast.
        var enumerator = new EndlessSlowEnumerator(itemCount: 1_000, delayPerItem: TimeSpan.FromMilliseconds(2));
        var budget = TimeSpan.FromSeconds(5);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await using var ctx = harness.CreateContext();
        var sut = BuildScanner(ctx, new FakeUsnReader([], 0), enumerator);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var act = async () => await sut.ScanVolumeAsync(volumeId, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        started.Stop();

        // Both halves matter: fast enough, AND cut short. Without the second assertion a machine
        // with a 1 ms timer resolution could walk all 1 000 items inside the budget and let the
        // unfixed code pass.
        enumerator.Remaining.Should().BeGreaterThan(
            0, "the walk must have been abandoned, not merely finished quickly");
        started.Elapsed.Should().BeLessThan(
            budget,
            "a stop during enumeration must not wait for the whole walk ({0} items still to come)",
            enumerator.Remaining);
    }

    /// <summary>C18: the token is not swapped for <c>CancellationToken.None</c> on the way down.</summary>
    [Theory]
    [InlineData(VolumeScanEngine.Enumeration, "exFAT")]
    [InlineData(VolumeScanEngine.UsnJournal, "NTFS")]
    public async Task The_caller_token_reaches_the_enumeration_port(VolumeScanEngine engine, string fileSystem)
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = SeedVolume(harness, engine, fileSystem);

        var enumerator = new TokenCapturingEnumerator();
        var usn = new TokenCapturingUsnReader();

        using var cts = new CancellationTokenSource();
        await using (var ctx = harness.CreateContext())
        {
            await BuildScanner(ctx, usn, enumerator).ScanVolumeAsync(volumeId, cts.Token);
        }

        var captured = engine == VolumeScanEngine.UsnJournal ? usn.Captured : enumerator.Captured;
        captured.Should().NotBeNull("the enumeration phase must be reached");
        captured!.Value.CanBeCanceled.Should().BeTrue("CancellationToken.None can never be cancelled");

        cts.Cancel();
        captured.Value.IsCancellationRequested.Should().BeTrue("it must be the caller's own token");
    }

    private static int SeedVolume(SqliteInMemoryContext harness, VolumeScanEngine engine, string fileSystem)
    {
        using var ctx = harness.CreateContext();
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

    private static ScanService BuildScanner(
        FileTracert.Data.FileTracertDbContext ctx, IUsnReader usn, IDirectoryEnumerator enumerator) =>
        new(
            ctx,
            new FakeVolumeProbe(new ProbedVolume(
                Guid, "SER", "Disk", "NTFS", IsRemovable: false,
                MountPoints: [@"X:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null)),
            usn,
            enumerator,
            new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
            new BulkIndexWriter(ctx),
            new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
            new FakeFileSearchIndex(),
            new FakeNotificationPublisher(),
            new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System),
            NullLogger<ScanService>.Instance);

    /// <summary>A long walk that never looks at the token — like a port that ignores it.</summary>
    private sealed class EndlessSlowEnumerator(int itemCount, TimeSpan delayPerItem) : IDirectoryEnumerator
    {
        private int _yielded;

        public int Remaining => itemCount - _yielded;

        public IEnumerable<ScanEntry> Enumerate(string mountRoot, string relativeRoot, CancellationToken ct)
        {
            for (var i = 0; i < itemCount; i++)
            {
                Thread.Sleep(delayPerItem);
                _yielded++;
                yield return new ScanEntry(
                    $@"Docs\f{i}.pdf", $"f{i}.pdf", false, 1, T, T, FileAttributes.Normal);
            }
        }

        public ulong? TryGetFileId(string absolutePath) => null;
    }

    private sealed class TokenCapturingEnumerator : IDirectoryEnumerator
    {
        public CancellationToken? Captured { get; private set; }

        public IEnumerable<ScanEntry> Enumerate(string mountRoot, string relativeRoot, CancellationToken ct)
        {
            Captured = ct;
            yield break;
        }

        public ulong? TryGetFileId(string absolutePath) => null;
    }

    private sealed class TokenCapturingUsnReader : IUsnReader
    {
        public CancellationToken? Captured { get; private set; }

        public bool SupportsUsn(string volumeGuid) => true;

        public UsnJournalState GetJournalState(string volumeGuid) =>
            new(1, FirstUsn: 0, NextUsn: 10, LowestValidUsn: 0);

        public void EnsureJournal(string volumeGuid) { }

        public IEnumerable<UsnEntry> ReadFullSnapshot(string volumeGuid, CancellationToken ct)
        {
            Captured = ct;
            return [];
        }

        public UsnChangeResult ReadChanges(string volumeGuid, long sinceUsn, ulong journalId, CancellationToken ct) =>
            throw new NotSupportedException();
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
