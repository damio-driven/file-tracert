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
/// A4, the hybrid engine. The MFT snapshot walks the WHOLE volume whatever the perimeter is, so a
/// volume watched at three folders pays for millions of records to index thousands (measured in
/// 14d). The hybrid walks what it was actually asked to look at — and still takes the journal
/// cursor first, so the incremental path starts anyway and whatever changed during the walk comes
/// back in the first delta.
///
/// <para>The rule for which walk runs is structural, not a "faster today" guess: the MFT dump and
/// an enumeration of the roots cover the same set only when a root IS the volume root. That is the
/// one case where the snapshot has nothing extra to walk, and the one case where it keeps
/// winning.</para>
/// </summary>
public sealed class HybridScanEngineTests
{
    private const string Guid = @"\\?\Volume{66666666-6666-6666-6666-666666666666}\";
    private static readonly DateTime T = new(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
    private const ulong RootFrn = (5UL << 48) | 5UL;

    private static ProbedVolume Probed => new(
        Guid, "SER-6", "Disk", "NTFS", IsRemovable: false,
        MountPoints: [@"X:\"], CapacityBytes: 5000, FreeBytes: 2000, PhysicalDiskId: null);

    /// <summary>Photos/ and Photos/Raw with one image each, as the enumerator reports them.</summary>
    private static List<ScanEntry> Entries(bool withIds = true) =>
    [
        new(@"Photos\Raw", "Raw", true, 0, T, T, FileAttributes.Directory, withIds ? 110UL : null),
        new(@"Photos\a.jpg", "a.jpg", false, 10, T, T, FileAttributes.Normal, withIds ? 200UL : null),
        new(@"Photos\Raw\c.jpg", "c.jpg", false, 30, T, T, FileAttributes.Normal, withIds ? 202UL : null),
    ];

    private static int Seed(SqliteInMemoryContext harness, string watchedRoot)
    {
        using var ctx = harness.CreateContext();
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
        ctx.SaveChanges();

        ctx.WatchedRoots.Add(new WatchedRoot { VolumeId = volume.Id, RelativePath = watchedRoot, IsActive = true });
        ctx.SaveChanges();
        return volume.Id;
    }

    /// <summary>The enumerator, with the watched root's own id answerable by path.</summary>
    private static FakeDirectoryEnumerator Enumerator(bool withIds = true) =>
        new(Entries(withIds))
        {
            FileIdsByPath = withIds
                ? new Dictionary<string, ulong> { ["Photos"] = 100UL }
                : new Dictionary<string, ulong>(),
        };

    private static async Task ScanAsync(
        SqliteInMemoryContext harness, int volumeId, IUsnReader usn, IDirectoryEnumerator enumerator)
    {
        await using var ctx = harness.CreateContext();
        var scan = new ScanService(
            ctx,
            new FakeVolumeProbe(Probed),
            usn,
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

    [Fact]
    public async Task A_watched_subfolder_is_walked_by_enumeration_and_still_gets_a_cursor()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, "Photos");

        var usn = new SnapshotCountingUsnReader { NextUsn = 500, Journal = 7 };
        await ScanAsync(harness, volumeId, usn, Enumerator());

        // The MFT was never walked: that is the whole point of the hybrid.
        usn.SnapshotReads.Should().Be(0);

        await using var read = harness.CreateContext();
        var volume = await read.Volumes.SingleAsync();
        volume.ScanEngine.Should().Be(VolumeScanEngine.Enumeration);
        volume.LastUsn.Should().Be(500);
        volume.UsnJournalId.Should().Be(unchecked((long)7UL));
    }

    [Fact]
    public async Task The_rows_carry_the_identity_the_delta_resolves_by()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, "Photos");

        await ScanAsync(
            harness, volumeId,
            new SnapshotCountingUsnReader { NextUsn = 500, Journal = 7 },
            Enumerator());

        await using var read = harness.CreateContext();

        // The walked directory, and the watched root itself — which the walk starts inside and
        // never yields, yet is the parent every record directly under it resolves against.
        var raw = await read.Directories.SingleAsync(d => d.MaterializedPath == @"Photos\Raw");
        raw.UsnFileRef.Should().Be(110);

        var root = await read.Directories.SingleAsync(d => d.MaterializedPath == "Photos");
        root.UsnFileRef.Should().Be(100, "the scan asks for the root's own id, which no walk hands it");

        var file = await read.Files.SingleAsync(f => f.Name == "a.jpg");
        file.UsnFileRef.Should().Be(200);
    }

    [Fact]
    public async Task A_volume_watched_at_its_root_keeps_the_mft_snapshot()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, "");

        var usn = new SnapshotCountingUsnReader
        {
            NextUsn = 500,
            Journal = 7,
            Snapshot =
            [
                new UsnEntry(100, RootFrn, "Photos", "Photos", true, null, FileAttributes.Directory, 1),
                new UsnEntry(200, 100, "a.jpg", @"Photos\a.jpg", false, null, FileAttributes.Normal, 2),
            ],
        };

        await ScanAsync(harness, volumeId, usn, new FakeDirectoryEnumerator([]));

        usn.SnapshotReads.Should().Be(1, "walking the whole MFT covers exactly the perimeter here");

        await using var read = harness.CreateContext();
        var volume = await read.Volumes.SingleAsync();
        volume.ScanEngine.Should().Be(VolumeScanEngine.UsnJournal);
        volume.LastUsn.Should().Be(500);
    }

    [Fact]
    public async Task No_journal_means_no_cursor_even_though_the_walk_is_the_same()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, "Photos");

        await ScanAsync(harness, volumeId, new ThrowingUsnReader(), Enumerator());

        await using var read = harness.CreateContext();
        var volume = await read.Volumes.SingleAsync();
        volume.LastFullScanUtc.Should().NotBeNull();
        volume.LastUsn.Should().BeNull("a position we could not read is not a cursor");
        volume.UsnJournalId.Should().BeNull();
    }

    [Fact]
    public async Task A_walk_that_captured_no_identity_refuses_to_leave_a_cursor()
    {
        // The dangerous shape: a cursor over rows the delta cannot resolve makes the incremental
        // path advance while indexing nothing, and say Applied while doing it.
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, "Photos");

        await ScanAsync(
            harness, volumeId,
            new SnapshotCountingUsnReader { NextUsn = 500, Journal = 7 },
            Enumerator(withIds: false));

        await using var read = harness.CreateContext();
        var volume = await read.Volumes.SingleAsync();
        volume.LastFullScanUtc.Should().NotBeNull();
        volume.LastUsn.Should().BeNull();
        volume.UsnJournalId.Should().BeNull();
    }

    [Fact]
    public async Task And_the_delta_then_declines_that_volume()
    {
        // The other half of the pairing: withholding the cursor is only a safeguard if it is what
        // the incremental pass actually consults. Together these two say "rows the delta cannot
        // place are never served to it" without either side having to trust the other's spelling.
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, "Photos");

        var reader = new ScriptedUsnReader { JournalId = 7 };
        reader.Script([], nextUsn: 500);

        await ScanAsync(harness, volumeId, reader, Enumerator(withIds: false));

        await using var ctx = harness.CreateContext();
        var applier = new UsnDeltaApplier(
            ctx,
            new FakeVolumeProbe(Probed),
            reader,
            new FakeFileMetadataReader(new Dictionary<string, FileMetadata>()),
            new BulkIndexWriter(ctx),
            new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
            new FakeFileSearchIndex(),
            NullLogger<UsnDeltaApplier>.Instance);

        var result = await applier.SyncVolumeAsync(volumeId, CancellationToken.None);

        result.Status.Should().Be(UsnSyncStatus.NotEligible);
        result.Reason.Should().Contain("checkpoint");
        reader.ReadChangesCalls.Should().Be(0);
    }

    /// <summary>
    /// Two paths, one file. NTFS hard links are not exotic — Git for Windows ships every
    /// <c>libexec\git-core</c> tool as a link to its twin in <c>bin</c>, the Python launcher does
    /// the same, and the enumeration walk reports BOTH, because both are paths and a Files row is
    /// a path. The MFT snapshot never produced this shape (it keeps one path per FRN, review item
    /// P1), so until the hybrid gave the enumeration walk the file reference number the merge's
    /// stated invariant — "a duplicate FRN is impossible" — held by accident.
    ///
    /// <para>The identity is a claim at most one path per volume holds: the first path walked
    /// keeps it, the others are tracked by path, which is exactly how EVERY enumeration-indexed
    /// row behaved before the hybrid. So no path is ever dropped, and no row loses anything it
    /// used to have.</para>
    /// </summary>
    [Fact]
    public async Task Two_hard_linked_paths_are_both_indexed_and_only_one_claims_the_identity()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, "Photos");

        await ScanAsync(harness, volumeId, new SnapshotCountingUsnReader(), HardLinkEnumerator());

        await using var read = harness.CreateContext();
        var files = await read.Files.OrderBy(f => f.Name).ToListAsync();

        files.Select(f => f.Name).Should().BeEquivalentTo(["a.jpg", "c.jpg", "link.jpg"],
            "a Files row is a PATH, and both hard-linked paths exist on disk");
        files.Where(f => f.UsnFileRef == 200).Should().ContainSingle(
            "the two hard-linked paths share one file reference, and the unique index says one row may hold it");
        files.Single(f => f.Name == "a.jpg").UsnFileRef.Should().Be(200, "the first path walked keeps the identity");
        files.Single(f => f.Name == "link.jpg").UsnFileRef.Should().BeNull("the other path is tracked by path, as before the hybrid");
    }

    /// <summary>
    /// And a re-scan converges instead of flapping: the same path keeps the identity, neither row
    /// is dropped, and nothing is marked absent — the walk saw both.
    /// </summary>
    [Fact]
    public async Task A_rescan_over_hard_links_keeps_both_rows_and_the_same_claim()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = Seed(harness, "Photos");

        await ScanAsync(harness, volumeId, new SnapshotCountingUsnReader(), HardLinkEnumerator());
        await ScanAsync(harness, volumeId, new SnapshotCountingUsnReader(), HardLinkEnumerator());

        await using var read = harness.CreateContext();
        var files = await read.Files.OrderBy(f => f.Name).ToListAsync();

        files.Should().HaveCount(3);
        files.Should().OnlyContain(f => f.IsPresent);
        files.Single(f => f.Name == "a.jpg").UsnFileRef.Should().Be(200);
        files.Single(f => f.Name == "link.jpg").UsnFileRef.Should().BeNull();
    }

    /// <summary>Photos/ with a.jpg and link.jpg as two names for the SAME file (one FRN).</summary>
    private static FakeDirectoryEnumerator HardLinkEnumerator() =>
        new([
            new(@"Photos\Raw", "Raw", true, 0, T, T, FileAttributes.Directory, 110UL),
            new(@"Photos\a.jpg", "a.jpg", false, 10, T, T, FileAttributes.Normal, 200UL),
            new(@"Photos\link.jpg", "link.jpg", false, 10, T, T, FileAttributes.Normal, 200UL),
            new(@"Photos\Raw\c.jpg", "c.jpg", false, 30, T, T, FileAttributes.Normal, 202UL),
        ])
        {
            FileIdsByPath = new Dictionary<string, ulong> { ["Photos"] = 100UL },
        };
}

/// <summary>A journal that answers, and counts how many times the MFT was actually walked.</summary>
internal sealed class SnapshotCountingUsnReader : IUsnReader
{
    public long NextUsn { get; init; } = 500;
    public ulong Journal { get; init; } = 7;
    public IReadOnlyList<UsnEntry> Snapshot { get; init; } = [];
    public int SnapshotReads { get; private set; }

    public bool SupportsUsn(string volumeGuid) => true;

    public UsnJournalState GetJournalState(string volumeGuid) =>
        new(Journal, FirstUsn: 0, NextUsn: NextUsn, LowestValidUsn: 0);

    public void EnsureJournal(string volumeGuid) { }

    public IEnumerable<UsnEntry> ReadFullSnapshot(string volumeGuid, CancellationToken ct)
    {
        SnapshotReads++;
        return Snapshot;
    }

    public UsnChangeResult ReadChanges(string volumeGuid, long sinceUsn, ulong journalId, CancellationToken ct) =>
        new([], NextUsn, RequiresFullRescan: false);
}
