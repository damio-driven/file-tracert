using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
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

/// <summary>
/// The assertion this whole step exists for: the short road and the long road must arrive at the
/// same catalog. Every case starts two databases from the SAME full scan of a starting world, then
/// takes one of them through a full re-scan of the changed world and the other through the USN
/// delta that describes the same change — and demands the rows come out identical.
///
/// <para>Two full scans rather than "one fresh scan of the end state" on purpose: a catalog has
/// history. A deleted file leaves a row flagged absent, an excluded one keeps its identity — none
/// of which a virgin scan would produce. Comparing against a virgin database would let the delta
/// be wrong in exactly the places that matter.</para>
/// </summary>
public sealed class UsnDeltaConvergenceTests
{
    private const string Guid = @"\\?\Volume{44444444-4444-4444-4444-444444444444}\";
    private static readonly DateTime T = new(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Root FRN as NTFS shapes it: MFT index 5 with a sequence number on top.</summary>
    private const ulong RootFrn = (5UL << 48) | 5UL;

    private static ProbedVolume Probed => new(
        Guid, "SER-4", "Disk", "NTFS", IsRemovable: false,
        MountPoints: [@"X:\"], CapacityBytes: 5000, FreeBytes: 2000, PhysicalDiskId: null);

    /// <summary>One filesystem object, as both engines would see it.</summary>
    private sealed record Item(
        ulong Frn,
        ulong ParentFrn,
        string Path,
        bool IsDirectory,
        long Size = 0,
        FileAttributes Attributes = FileAttributes.Normal)
    {
        public string Name => ScanPath.Name(Path);
    }

    // ── the cases ─────────────────────────────────────────────────────────────

    private static Item Dir(ulong frn, ulong parent, string path) =>
        new(frn, parent, path, IsDirectory: true, Attributes: FileAttributes.Directory);

    private static Item File(ulong frn, ulong parent, string path, long size) =>
        new(frn, parent, path, IsDirectory: false, size);

    /// <summary>Photos/ with two images and one filtered-out text file.</summary>
    private static List<Item> StartingWorld() =>
    [
        Dir(100, RootFrn, "Photos"),
        Dir(110, 100, @"Photos\Raw"),
        File(200, 100, @"Photos\a.jpg", 10),
        File(201, 100, @"Photos\notes.txt", 5),
        File(202, 110, @"Photos\Raw\c.cr2", 30),
    ];

    [Fact]
    public async Task A_created_file_converges()
    {
        var before = StartingWorld();
        var after = new List<Item>(before) { File(203, 100, @"Photos\new.jpg", 44) };

        await AssertConvergesAsync(before, after, [Change(after, 203, UsnReason.FileCreate | UsnReason.Close)]);
    }

    [Fact]
    public async Task A_renamed_file_converges()
    {
        var before = StartingWorld();
        var after = Replace(before, 200, i => i with { Path = @"Photos\renamed.jpg" });

        // The journal emits the old name and then the new one; only the last record survives the
        // coalescing, which is exactly what makes a rename "this file is now called that".
        await AssertConvergesAsync(before, after,
        [
            Change(before, 200, UsnReason.RenameOldName, oldName: "a.jpg"),
            Change(after, 200, UsnReason.RenameNewName | UsnReason.Close),
        ]);
    }

    [Fact]
    public async Task A_file_moved_to_another_folder_converges()
    {
        var before = StartingWorld();
        var after = Replace(before, 200, i => i with { ParentFrn = 110, Path = @"Photos\Raw\a.jpg" });

        await AssertConvergesAsync(before, after,
        [
            Change(before, 200, UsnReason.RenameOldName, oldName: "a.jpg"),
            Change(after, 200, UsnReason.RenameNewName | UsnReason.Close),
        ]);
    }

    [Fact]
    public async Task A_deleted_file_converges_to_absent_and_not_to_a_deleted_row()
    {
        var before = StartingWorld();
        var after = before.Where(i => i.Frn != 202).ToList();

        await AssertConvergesAsync(before, after,
            [Change(before, 202, UsnReason.FileDelete | UsnReason.Close)],
            extra: async db =>
            {
                // Soft, never a delete (§6): the row survives, flagged, so a queued operation
                // still finds the identity it references.
                var gone = await db.Files.SingleAsync(f => f.UsnFileRef == 202);
                gone.IsPresent.Should().BeFalse();
                gone.IsIncluded.Should().BeTrue("presence and inclusion are different facts");
            });
    }

    [Fact]
    public async Task A_new_folder_and_the_file_inside_it_converge()
    {
        var before = StartingWorld();
        var after = new List<Item>(before)
        {
            Dir(120, 100, @"Photos\2026"),
            File(204, 120, @"Photos\2026\shot.jpg", 77),
        };

        // The folder's parent is in the catalog, the file's parent is only in the delta: the
        // resolver has to stitch the two halves together.
        await AssertConvergesAsync(before, after,
        [
            Change(after, 120, UsnReason.FileCreate | UsnReason.Close),
            Change(after, 204, UsnReason.FileCreate | UsnReason.Close),
        ]);
    }

    [Fact]
    public async Task A_file_renamed_out_of_the_allow_list_converges_to_absent()
    {
        var before = StartingWorld();
        var after = Replace(before, 200, i => i with { Path = @"Photos\a.bak" });

        // The scan reaches this by never seeing 'a.jpg' again; the delta reaches it by being told
        // the file is called something the allow-list refuses. Same row, same verdict.
        await AssertConvergesAsync(before, after,
        [
            Change(before, 200, UsnReason.RenameOldName, oldName: "a.jpg"),
            Change(after, 200, UsnReason.RenameNewName | UsnReason.Close),
        ]);
    }

    [Fact]
    public async Task A_file_moved_into_a_hidden_folder_converges_to_absent_at_its_old_path()
    {
        var before = new List<Item>(StartingWorld()) { Dir(130, 100, @"Photos\Private") };
        var hidden = before.Single(i => i.Frn == 130) with
        {
            Attributes = FileAttributes.Directory | FileAttributes.Hidden,
        };

        var after = Replace(before, 130, _ => hidden);
        after = after.Select(i => i.Frn == 200
            ? i with { ParentFrn = 130, Path = @"Photos\Private\a.jpg" }
            : i).ToList();

        await AssertConvergesAsync(before, after,
        [
            Change(after, 130, UsnReason.BasicInfoChange | UsnReason.Close),
            Change(before, 200, UsnReason.RenameOldName, oldName: "a.jpg"),
            Change(after, 200, UsnReason.RenameNewName | UsnReason.Close),
        ],
        extra: async db =>
        {
            // Named, not left to the equivalence: convergence alone would be satisfied by both
            // roads being wrong in the same way. The row is at its OLD path as far as the catalog
            // is concerned, and the file is not there any more — so this is absence, not exclusion.
            var moved = await db.Files.SingleAsync(f => f.UsnFileRef == 200);
            moved.IsPresent.Should().BeFalse();
            moved.ExcludedByScan.Should().BeFalse("nothing excluded the row where it still sits");
        });
    }

    [Fact]
    public async Task An_untouched_catalog_is_left_completely_alone()
    {
        var world = StartingWorld();

        // The one thing a delta must never do: read "not mentioned" as "not there". Every row of
        // the volume is missing from this delta, and every row must survive it untouched.
        await AssertConvergesAsync(world, world, [Change(world, 201, UsnReason.DataOverwrite | UsnReason.Close)]);
    }

    // ── which cause the delta writes ──────────────────────────────────────────

    /// <summary>
    /// A folder in the catalog, two levels of rows under it, and one of those files touched by the
    /// journal while the folder is outside the perimeter. What is asserted is not that the row goes
    /// out — the convergence cases above already cover that — but WHICH of the four columns of §6
    /// records it, because the four are undone by different owners and getting the mapping wrong is
    /// silent, permanent and invisible to the screen.
    ///
    /// <para><b>Why this needs its own pair of tests rather than a convergence case.</b> The delta's
    /// per-cause buckets and the single transaction that writes them are new in step 16, and the
    /// suite could not tell <c>ExcludedByPath</c> from <c>ExcludedByScan</c> here: every convergence
    /// case seeds <c>ExcludedPaths = []</c>, so the path branch was never executed at all, and
    /// swapping the two columns left the suite green. Convergence is the wrong instrument for the
    /// path half in particular: reaching it needs a folder whose CATALOG path already carries the
    /// segment, and a full re-scan of the same world reaches the row through the directory area it
    /// skipped rather than through this loop — the two roads agree on the column and would agree on
    /// the wrong column just as happily.</para>
    ///
    /// <para><b>Why the parent of the touched file is two levels down.</b> A file whose direct
    /// parent is in the delta has no <c>ParentId</c> from the catalog (the resolver takes the
    /// delta's copy of that directory), so it resolves to no directory row and is treated as GONE,
    /// not as excluded. The loop that writes causes is reached only by a row whose own directory is
    /// catalog-resident and untouched by this delta and sits under one the delta just excluded.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_row_under_a_path_excluded_folder_records_the_cause_the_settings_can_undo()
    {
        using var harness = new SqliteInMemoryContext();
        var reader = ReaderFor(NestedWorld());
        var volumeId = await SeedAndScanAsync(harness, reader, NestedWorld());

        // The segment arrives after the volume was last scanned, which is the only way a row can be
        // sitting INCLUDED under a folder whose path is excluded. Reachable in production: a
        // completed MoveFile lands a row in such a folder and IndexUpdater does not write the path
        // cause (declared as a known limit of this round), and reconciliation only ever re-decides
        // what is under a watched root at the moment Save is pressed.
        await SetExcludedPathsAsync(harness, "Cache");

        var world = NestedWorld();
        reader.Changes =
        [
            Change(world, 140, UsnReason.BasicInfoChange | UsnReason.Close),
            Change(world, 205, UsnReason.DataOverwrite | UsnReason.Close),
        ];
        reader.NextUsn = 900;

        await using (var ctx = harness.CreateContext())
        {
            var result = await BuildApplier(ctx, reader, MetadataFor(world)).SyncVolumeAsync(volumeId, default);
            result.Status.Should().Be(UsnSyncStatus.Applied);
        }

        await using var read = harness.CreateContext();
        var row = await read.Files.SingleAsync(f => f.UsnFileRef == 205);
        row.IsIncluded.Should().BeFalse("a segment of its path is on the excluded list");
        row.ExcludedByPath.Should().BeTrue(
            "the cause is a fact of the SETTINGS: dropping the segment must re-admit the row with no scan");
        row.ExcludedByScan.Should().BeFalse(
            "writing the attribute cause here would put the row behind a column reconciliation may never " +
            "touch — removing the segment would then never bring it back, and only a full scan could");
        row.IsPresent.Should().BeTrue("an exclusion is not an absence (§6)");
    }

    /// <summary>
    /// The mirror of the case above, and the half that makes the pair a real guard: the same shape
    /// with the folder turning HIDDEN instead must land in <c>ExcludedByScan</c>. Either column
    /// written in the other's place reddens one of these two.
    /// </summary>
    [Fact]
    public async Task A_row_under_a_newly_hidden_folder_records_the_cause_only_a_scan_can_undo()
    {
        using var harness = new SqliteInMemoryContext();
        var reader = ReaderFor(NestedWorld());
        var volumeId = await SeedAndScanAsync(harness, reader, NestedWorld());

        var after = Replace(NestedWorld(), 140, i => i with
        {
            Attributes = FileAttributes.Directory | FileAttributes.Hidden,
        });

        reader.Changes =
        [
            Change(after, 140, UsnReason.BasicInfoChange | UsnReason.Close),
            Change(after, 205, UsnReason.DataOverwrite | UsnReason.Close),
        ];
        reader.NextUsn = 900;

        await using (var ctx = harness.CreateContext())
        {
            var result = await BuildApplier(ctx, reader, MetadataFor(after)).SyncVolumeAsync(volumeId, default);
            result.Status.Should().Be(UsnSyncStatus.Applied);
        }

        await using var read = harness.CreateContext();
        var row = await read.Files.SingleAsync(f => f.UsnFileRef == 205);
        row.IsIncluded.Should().BeFalse();
        row.ExcludedByScan.Should().BeTrue(
            "no setting says whether that folder is still hidden, so only another scan may retract it");
        row.ExcludedByPath.Should().BeFalse(
            "nothing in this row's path says so — recording it here would let a settings change re-admit " +
            "the content of a hidden folder, which is the regression 11h exists to prevent");
        row.IsPresent.Should().BeTrue("an exclusion is not an absence (§6)");
    }

    /// <summary>
    /// A folder with a nested folder inside it, so the file's own directory row survives a delta
    /// that only names the folder ABOVE it — see the note on the first test for why the extra level
    /// is what makes the cause-writing loop reachable at all.
    /// </summary>
    private static List<Item> NestedWorld() =>
    [
        Dir(100, RootFrn, "Photos"),
        Dir(140, 100, @"Photos\Cache"),
        Dir(150, 140, @"Photos\Cache\Sub"),
        File(205, 150, @"Photos\Cache\Sub\a.jpg", 12),
    ];

    private static async Task SetExcludedPathsAsync(SqliteInMemoryContext harness, params string[] segments)
    {
        await using var ctx = harness.CreateContext();
        var settings = await ctx.AppSettings.FirstAsync();
        settings.ExcludedPaths = segments.ToList();
        await ctx.SaveChangesAsync();
    }

    // ── the cursor ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_cursor_advances_only_after_the_delta_is_applied()
    {
        using var harness = new SqliteInMemoryContext();
        var reader = ReaderFor(StartingWorld());
        var volumeId = await SeedAndScanAsync(harness, reader, StartingWorld());

        var after = new List<Item>(StartingWorld()) { File(203, 100, @"Photos\new.jpg", 44) };
        reader.Changes = [Change(after, 203, UsnReason.FileCreate | UsnReason.Close)];
        reader.NextUsn = 900;

        await using (var ctx = harness.CreateContext())
        {
            var result = await BuildApplier(ctx, reader, MetadataFor(after)).SyncVolumeAsync(volumeId, default);
            result.Status.Should().Be(UsnSyncStatus.Applied);
        }

        await using (var read = harness.CreateContext())
        {
            var volume = await read.Volumes.SingleAsync();
            volume.LastUsn.Should().Be(900);
            volume.UsnJournalId.Should().Be(7);
        }

        // The cursor moved, so the next pass resumes from there instead of replaying the delta.
        await using (var ctx = harness.CreateContext())
        {
            var result = await BuildApplier(ctx, reader, MetadataFor(after)).SyncVolumeAsync(volumeId, default);
            result.Status.Should().Be(UsnSyncStatus.UpToDate);
        }

        reader.Resumed.Should().Equal((500, 7ul), (900, 7ul));
    }

    /// <summary>
    /// Re-applying the same delta must be a no-op, because that is what a crash between the writes
    /// and the checkpoint produces. Everything the pass does is keyed on the FRN or set to a
    /// constant, so the second run finds the work already done.
    /// </summary>
    [Fact]
    public async Task Replaying_the_same_delta_changes_nothing()
    {
        using var harness = new SqliteInMemoryContext();
        var reader = ReaderFor(StartingWorld());
        var volumeId = await SeedAndScanAsync(harness, reader, StartingWorld());

        var after = new List<Item>(StartingWorld().Where(i => i.Frn != 202))
        {
            File(203, 100, @"Photos\new.jpg", 44),
        };
        reader.Changes =
        [
            Change(StartingWorld(), 202, UsnReason.FileDelete | UsnReason.Close),
            Change(after, 203, UsnReason.FileCreate | UsnReason.Close),
        ];
        reader.NextUsn = 900;

        await using (var ctx = harness.CreateContext())
        {
            await BuildApplier(ctx, reader, MetadataFor(after)).SyncVolumeAsync(volumeId, default);
        }

        var once = await SnapshotAsync(harness);

        // Rewind the cursor by hand: the delta is offered a second time, exactly as it would be
        // after a crash between the last write and the checkpoint.
        await using (var ctx = harness.CreateContext())
        {
            var volume = await ctx.Volumes.SingleAsync();
            volume.LastUsn = 500;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = harness.CreateContext())
        {
            await BuildApplier(ctx, reader, MetadataFor(after)).SyncVolumeAsync(volumeId, default);
        }

        (await SnapshotAsync(harness)).Should().BeEquivalentTo(once);
    }

    [Fact]
    public async Task A_journal_that_no_longer_covers_the_cursor_asks_for_a_full_scan()
    {
        using var harness = new SqliteInMemoryContext();
        var reader = ReaderFor(StartingWorld());
        var volumeId = await SeedAndScanAsync(harness, reader, StartingWorld());

        // The journal was deleted and recreated: same volume, brand-new numbering.
        reader.JournalId = 99;

        await using (var ctx = harness.CreateContext())
        {
            var result = await BuildApplier(ctx, reader, MetadataFor(StartingWorld()))
                .SyncVolumeAsync(volumeId, default);
            result.Status.Should().Be(UsnSyncStatus.RescanRequired);
        }

        await using (var read = harness.CreateContext())
        {
            // Dropped, not kept: otherwise every cycle would re-discover the same dead cursor and
            // ask for the same rescan for ever.
            var volume = await read.Volumes.SingleAsync();
            volume.LastUsn.Should().BeNull();
            volume.UsnJournalId.Should().BeNull();
            volume.LastFullScanUtc.Should().NotBeNull("the index stays usable while the rescan is pending");
        }
    }

    [Fact]
    public async Task A_volume_last_scanned_by_enumeration_is_not_eligible()
    {
        using var harness = new SqliteInMemoryContext();
        var reader = ReaderFor(StartingWorld());
        var volumeId = await SeedAndScanAsync(harness, reader, StartingWorld());

        await using (var ctx = harness.CreateContext())
        {
            // Its directory rows would carry no file references, so not a single path could be
            // placed — the delta must decline rather than guess.
            var volume = await ctx.Volumes.SingleAsync();
            volume.ScanEngine = VolumeScanEngine.Enumeration;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = harness.CreateContext())
        {
            var result = await BuildApplier(ctx, reader, MetadataFor(StartingWorld()))
                .SyncVolumeAsync(volumeId, default);
            result.Status.Should().Be(UsnSyncStatus.NotEligible);
            result.Reason.Should().Contain("enumeration");
        }

        reader.ReadChangesCalls.Should().Be(0, "an ineligible volume must not even open the journal");
    }

    // ── harness ───────────────────────────────────────────────────────────────

    private static async Task AssertConvergesAsync(
        List<Item> before,
        List<Item> after,
        List<UsnChangeRecord> changes,
        Func<FileTracertDbContext, Task>? extra = null)
    {
        // Long road: full scan of the starting world, then a full re-scan of the changed one.
        using var viaScan = new SqliteInMemoryContext();
        var scanReader = ReaderFor(before);
        var scanVolumeId = await SeedAndScanAsync(viaScan, scanReader, before);
        var rescanReader = ReaderFor(after);
        await ScanAsync(viaScan, scanVolumeId, rescanReader, MetadataFor(after));

        // Short road: the same starting scan, then the delta that describes the same change.
        using var viaDelta = new SqliteInMemoryContext();
        var deltaReader = ReaderFor(before);
        var deltaVolumeId = await SeedAndScanAsync(viaDelta, deltaReader, before);
        deltaReader.Changes = changes;
        deltaReader.NextUsn = 900;

        await using (var ctx = viaDelta.CreateContext())
        {
            var result = await BuildApplier(ctx, deltaReader, MetadataFor(after))
                .SyncVolumeAsync(deltaVolumeId, default);
            result.Status.Should().Be(UsnSyncStatus.Applied);
        }

        (await SnapshotAsync(viaDelta)).Should().BeEquivalentTo(await SnapshotAsync(viaScan));

        if (extra is not null)
        {
            await using var read = viaDelta.CreateContext();
            await extra(read);
        }
    }

    /// <summary>
    /// Everything about the catalog a scan is allowed to decide, and nothing that is merely a
    /// timestamp of when it happened.
    /// </summary>
    private static async Task<object> SnapshotAsync(SqliteInMemoryContext harness)
    {
        await using var read = harness.CreateContext();

        var files = await read.Files.AsNoTracking()
            .Select(f => new
            {
                f.UsnFileRef,
                f.Name,
                f.Extension,
                f.Category,
                f.SizeBytes,
                f.IsIncluded,
                f.IsPresent,
                f.ExcludedByType,
                f.ExcludedByRoot,
                f.ExcludedByScan,
                f.ExcludedByPath,
                Directory = f.Directory.MaterializedPath,
            })
            .OrderBy(f => f.Directory).ThenBy(f => f.Name)
            .ToListAsync();

        var directories = await read.Directories.AsNoTracking()
            .Select(d => new { d.MaterializedPath, d.UsnFileRef, d.IsPresent, d.IsMaterialized })
            .OrderBy(d => d.MaterializedPath)
            .ToListAsync();

        return new { Files = files, Directories = directories };
    }

    private static ScriptedUsnReader ReaderFor(List<Item> world) => new()
    {
        Snapshot = world
            .Select(i => new UsnEntry(
                i.Frn, i.ParentFrn, i.Name, i.Path, i.IsDirectory,
                SizeBytes: null, i.Attributes, Usn: 1))
            .ToList(),
    };

    private static FakeFileMetadataReader MetadataFor(List<Item> world) =>
        new(world.Where(i => !i.IsDirectory)
            .ToDictionary(i => i.Path, i => new FileMetadata(i.Size, T, T)));

    /// <summary>
    /// One journal record. The incremental reader only fills the leaf name into
    /// <see cref="UsnEntry.RelativePath"/> — parents are normally outside the delta — so the
    /// fixture is built the same way: anything that read the full path here would be testing a
    /// field the product deliberately does not trust.
    /// </summary>
    private static UsnChangeRecord Change(
        List<Item> world, ulong frn, UsnReason reason, string? oldName = null)
    {
        var item = world.Single(i => i.Frn == frn);
        var entry = new UsnEntry(
            item.Frn, item.ParentFrn, item.Name, item.Name, item.IsDirectory,
            SizeBytes: null, item.Attributes, Usn: 600);

        var isRename = (reason & (UsnReason.RenameOldName | UsnReason.RenameNewName)) != 0;
        return new UsnChangeRecord(entry, reason, isRename, oldName);
    }

    private static async Task<int> SeedAndScanAsync(
        SqliteInMemoryContext harness, ScriptedUsnReader reader, List<Item> world)
    {
        int volumeId;
        using (var ctx = harness.CreateContext())
        {
            ctx.AppSettings.RemoveRange(ctx.AppSettings);
            ctx.AppSettings.Add(new AppSettings
            {
                DefaultExtensionFilter = ["jpg", "cr2"],
                ExcludedPaths = [],
                ApiToken = "token",
                SpaceMarginPercent = 5,
            });

            var volume = new Volume
            {
                VolumeGuid = Guid,
                FileSystem = "NTFS",
                ScanEngine = VolumeScanEngine.UsnJournal,
                IsOnline = true,
            };
            ctx.Volumes.Add(volume);
            await ctx.SaveChangesAsync();
            volumeId = volume.Id;

            ctx.WatchedRoots.Add(new WatchedRoot { VolumeId = volumeId, RelativePath = "", IsActive = true });
            await ctx.SaveChangesAsync();
        }

        await ScanAsync(harness, volumeId, reader, MetadataFor(world));
        return volumeId;
    }

    private static async Task ScanAsync(
        SqliteInMemoryContext harness, int volumeId, ScriptedUsnReader reader, FakeFileMetadataReader metadata)
    {
        await using var ctx = harness.CreateContext();
        var scan = new ScanService(
            ctx,
            new FakeVolumeProbe(Probed),
            reader,
            new FakeDirectoryEnumerator([]),
            metadata,
            new BulkIndexWriter(ctx),
            new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
            new FakeFileSearchIndex(),
            new FakeNotificationPublisher(),
            new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System),
            NullLogger<ScanService>.Instance);

        await scan.ScanVolumeAsync(volumeId, CancellationToken.None);
    }

    private static UsnDeltaApplier BuildApplier(
        FileTracertDbContext ctx, ScriptedUsnReader reader, FakeFileMetadataReader metadata) =>
        new(ctx,
            new FakeVolumeProbe(Probed),
            reader,
            metadata,
            new BulkIndexWriter(ctx),
            new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
            new FakeFileSearchIndex(),
            NullLogger<UsnDeltaApplier>.Instance);

    private static List<Item> Replace(List<Item> world, ulong frn, Func<Item, Item> change) =>
        world.Select(i => i.Frn == frn ? change(i) : i).ToList();
}
