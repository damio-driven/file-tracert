using FileTracert.Business.Filtering;
using FileTracert.Business.Scanning;
using FileTracert.Business.Setup;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using FileTracert.Data.Search;
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
        reader.Script(
        [
            Change(world, 140, UsnReason.BasicInfoChange | UsnReason.Close),
            Change(world, 205, UsnReason.DataOverwrite | UsnReason.Close),
        ], nextUsn: 900);

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

        reader.Script(
        [
            Change(after, 140, UsnReason.BasicInfoChange | UsnReason.Close),
            Change(after, 205, UsnReason.DataOverwrite | UsnReason.Close),
        ], nextUsn: 900);

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

    // ── the subtree that leaves the perimeter (A3) ────────────────────────────

    /// <summary>
    /// Photos/Cache/Sub with a file at each level under Cache, plus one OUTSIDE Cache. Almost
    /// nothing here is excluded to begin with: the rows these cases are about are rows the catalog
    /// ALREADY holds, indexed by the starting scan.
    ///
    /// <para>No <c>.tmp</c> here, and the reason is worth writing down because it is the obvious
    /// way to build the "already excluded, must still learn this cause" row and it does not work: a
    /// file outside the allow-list is never indexed in the FIRST place, so a fresh scan leaves no
    /// row at all to carry <c>ExcludedByType</c>. Such a row only exists where the filter was once
    /// wider and then narrowed — which is a reconciliation, and that is how
    /// <see cref="A_folder_both_rules_refuse_records_both_causes_on_the_rows_below"/> reaches
    /// it.</para>
    /// </summary>
    private static List<Item> SubtreeWorld() =>
    [
        Dir(100, RootFrn, "Photos"),
        Dir(140, 100, @"Photos\Cache"),
        Dir(150, 140, @"Photos\Cache\Sub"),
        File(200, 100, @"Photos\keep.jpg", 10),
        File(205, 140, @"Photos\Cache\b.jpg", 12),
        File(206, 150, @"Photos\Cache\Sub\c.jpg", 13),
    ];

    /// <summary>
    /// The defect A3 names, put to the only instrument that settles it. A folder in the catalog
    /// turns HIDDEN and <b>nothing else happens</b>: not one of the files under it is renamed,
    /// written or deleted, so not one of them appears in the journal.
    ///
    /// <para>The long road excludes them anyway, because a scan asks the perimeter about every
    /// directory of the catalog when it closes and each descendant produces its own skipped area.
    /// The short road cannot: it sees what changed. Before the subtree pass the two databases
    /// disagreed on every row under that folder — the exclusion was silently not applied, which is
    /// the worst shape of failure this project recognises, because the user believes they decided
    /// something.</para>
    /// </summary>
    [Fact]
    public async Task A_folder_that_becomes_hidden_takes_the_rows_already_in_the_catalog_with_it()
    {
        var before = SubtreeWorld();
        var after = Replace(before, 140, i => i with
        {
            Attributes = FileAttributes.Directory | FileAttributes.Hidden,
        });

        await AssertConvergesAsync(before, after,
            [Change(after, 140, UsnReason.BasicInfoChange | UsnReason.Close)],
            extra: async db =>
            {
                // Named, not left to the equivalence: both roads reach these rows through code
                // that writes a COLUMN, and two roads can agree on the wrong column as happily as
                // on the right one (the correction 14d had to make to its own hidden-folder case).
                foreach (var frn in (long[])[205, 206])
                {
                    var row = await db.Files.SingleAsync(f => f.UsnFileRef == frn);
                    row.IsIncluded.Should().BeFalse("the folder above it left the perimeter");
                    row.ExcludedByScan.Should().BeTrue(
                        "no setting says whether that folder is still hidden — only another scan may retract it");
                    row.ExcludedByPath.Should().BeFalse(
                        "nothing in this row's path says so; recording it here would let a settings change " +
                        "walk the content of a hidden folder back into the Catalog");
                    row.IsPresent.Should().BeTrue("an exclusion is not an absence (§6)");
                }

                var outside = await db.Files.SingleAsync(f => f.UsnFileRef == 200);
                outside.IsIncluded.Should().BeTrue("it does not live under the folder that went hidden");
            });
    }

    /// <summary>
    /// The other cause on the same mechanism: the folder is not hidden, it is under a segment the
    /// user has just excluded. Deliberately NOT a convergence case — reaching it needs the setting
    /// to change between the two scans, and a re-scan reaches those rows through the directory area
    /// it skipped rather than through this pass, so the column would be asserted twice by the same
    /// kind of code. Here the rows are named directly, and the pair with the case above is what
    /// makes either column swapped a red.
    /// </summary>
    [Fact]
    public async Task A_folder_that_falls_under_a_newly_excluded_segment_takes_its_catalogued_rows_with_it()
    {
        using var harness = new SqliteInMemoryContext();
        var world = SubtreeWorld();
        var reader = ReaderFor(world);
        var volumeId = await SeedAndScanAsync(harness, reader, world);

        await SetExcludedPathsAsync(harness, "Cache");

        // Only the FOLDER is in this delta. Neither file under it is named by any record, which is
        // what separates this from the per-file loop of ReconcileAsync.
        reader.Script([Change(world, 140, UsnReason.BasicInfoChange | UsnReason.Close)], nextUsn: 900);

        await using (var ctx = harness.CreateContext())
        {
            var result = await BuildApplier(ctx, reader, MetadataFor(world)).SyncVolumeAsync(volumeId, default);
            result.Status.Should().Be(UsnSyncStatus.Applied);
        }

        await using var read = harness.CreateContext();
        foreach (var frn in (long[])[205, 206])
        {
            var row = await read.Files.SingleAsync(f => f.UsnFileRef == frn);
            row.IsIncluded.Should().BeFalse("a segment of its path is on the excluded list");
            row.ExcludedByPath.Should().BeTrue(
                "the cause is a fact of the SETTINGS: dropping the segment must re-admit the row with no scan");
            row.ExcludedByScan.Should().BeFalse(
                "writing the attribute cause here would put the row behind a column reconciliation never " +
                "touches, and only a full scan could ever bring it back");
            row.IsPresent.Should().BeTrue("an exclusion is not an absence (§6)");
        }
    }

    /// <summary>
    /// The half of the subtree pass the user actually SEES, and the half no equivalence case can
    /// reach: the search index. <see cref="SnapshotAsync"/> compares catalog rows and never looks
    /// at FTS5, and every other case here hands the applier a <c>FakeFileSearchIndex</c> — so
    /// deleting the <c>SyncDirectoriesAsync</c> call left the whole file green while the method's
    /// own documentation promised "findable in Search" was fixed.
    ///
    /// <para>The real <see cref="FileSearchIndex"/> on BOTH steps, which is what makes the
    /// assertion mean anything: with the starting scan on the fake, the table is empty when the
    /// delta runs and "the excluded rows are gone from the index" is vacuously true of an index
    /// that never had them.</para>
    ///
    /// <para>The second half of the claim is the row OUTSIDE the folder. A pass that pruned too
    /// widely — a volume-wide prune, say — would satisfy the first half perfectly.</para>
    /// </summary>
    [Fact]
    public async Task An_excluded_subtree_leaves_the_search_index_and_takes_nothing_else_with_it()
    {
        using var harness = new SqliteInMemoryContext();
        using (var create = harness.CreateContext())
        {
            SqliteFts.Create(create);
        }

        var before = SubtreeWorld();
        var reader = ReaderFor(before);
        var volumeId = await SeedAndScanAsync(harness, reader, before, realSearchIndex: true);

        SqliteFts.Rows(harness).Select(r => r.Path).Should().BeEquivalentTo(
            [@"Photos\keep.jpg", @"Photos\Cache\b.jpg", @"Photos\Cache\Sub\c.jpg"],
            "the starting scan indexed everything inside the perimeter — .tmp was never in the allow-list");

        var after = Replace(before, 140, i => i with
        {
            Attributes = FileAttributes.Directory | FileAttributes.Hidden,
        });
        reader.Script([Change(after, 140, UsnReason.BasicInfoChange | UsnReason.Close)], nextUsn: 900);

        await using (var ctx = harness.CreateContext())
        {
            var result = await BuildApplier(ctx, reader, MetadataFor(after), new FileSearchIndex(ctx))
                .SyncVolumeAsync(volumeId, default);
            result.Status.Should().Be(UsnSyncStatus.Applied);
        }

        SqliteFts.Rows(harness).Select(r => r.Path).Should().Equal([@"Photos\keep.jpg"],
            "a row the perimeter now excludes must stop answering searches, and a row outside the "
            + "folder must go on answering them");
    }

    /// <summary>
    /// One folder, BOTH perimeter rules: hidden, and under a segment the user excluded in the same
    /// window. It is the case the two structural decisions of this round were made for, and until
    /// now nothing in the suite was holding either of them.
    ///
    /// <para><b>The causes sum.</b> The pass writes one UPDATE per cause the verdict carries, and
    /// stopping at the first is invisible to every other case here because every other case has a
    /// verdict of exactly one cause. It is not invisible in production: the cause that survives is
    /// undone by its owner, and the row comes back with the other one still true of it — a hidden
    /// folder's content walked back into the Catalog by dropping a path segment, which is the 11h
    /// regression reached through a new door.</para>
    ///
    /// <para><b>The guard is the cause's own column, never <c>IsIncluded</c>.</b> These rows are
    /// ALREADY out when the delta runs — the Setup save in <c>between</c> is a real
    /// <c>FilterReconciler</c> pass, so they carry <c>ExcludedByPath = 1, IsIncluded = 0</c> — and
    /// they still have to learn the attribute cause. A guard reading <c>IsIncluded</c> alone, the
    /// mistake 11h exists to remember, skips every one of them; the full re-scan stamps them,
    /// because its guard is the cause's own column too. That difference is the divergence.</para>
    ///
    /// <para>The setting has to change BETWEEN the two scans, and that is not convenience: with the
    /// segment already excluded at the starting scan, the perimeter of that scan would have refused
    /// the folder and the rows under it would never have been indexed at all — there would be no
    /// catalogued row for this pass to reach.</para>
    /// </summary>
    [Fact]
    public async Task A_folder_both_rules_refuse_records_both_causes_on_the_rows_below()
    {
        var before = SubtreeWorld();
        var after = Replace(before, 140, i => i with
        {
            Attributes = FileAttributes.Directory | FileAttributes.Hidden,
        });

        await AssertConvergesAsync(before, after,
            [Change(after, 140, UsnReason.BasicInfoChange | UsnReason.Close)],
            between: ExcludeTheCacheSegmentAsync,
            extra: async db =>
            {
                foreach (var frn in (long[])[205, 206])
                {
                    var row = await db.Files.SingleAsync(f => f.UsnFileRef == frn);
                    row.IsIncluded.Should().BeFalse();
                    row.ExcludedByPath.Should().BeTrue(
                        "a segment of its path is excluded, and reconciliation must be able to retract that "
                        + "on its own");
                    row.ExcludedByScan.Should().BeTrue(
                        "the folder is also hidden, and only another scan may ever retract that — dropping "
                        + "the segment must NOT be enough to bring the row back");
                    row.IsPresent.Should().BeTrue("an exclusion is not an absence (§6)");
                }
            });
    }

    /// <summary>
    /// A Setup save that adds <c>Cache</c> to the excluded segments, through the real
    /// <see cref="FilterReconciler"/> and the real <see cref="FileSearchIndex"/> — that is, the A2
    /// road of this same step. Nothing is hand-written into the columns: the rows arrive at the
    /// delta in the state the product would have put them in.
    /// </summary>
    private static async Task ExcludeTheCacheSegmentAsync(SqliteInMemoryContext harness)
    {
        using (var create = harness.CreateContext())
        {
            SqliteFts.Create(create);
        }

        await SetExcludedPathsAsync(harness, "Cache");

        await using var ctx = harness.CreateContext();
        var settings = await ctx.AppSettings.FirstAsync();
        var root = await ctx.WatchedRoots.FirstAsync();

        await new FilterReconciler(ctx, new FileSearchIndex(ctx)).ReconcileRootAsync(
            root, EffectiveFilterBuilder.Build(settings, root.FilterOverrideJson), CancellationToken.None);
    }

    /// <summary>
    /// The reason the subtree pass runs BEFORE the absence passes, made into a case: a file that is
    /// genuinely deleted from a folder that goes hidden in the same delta.
    ///
    /// <para>A full scan calls that EXCLUDED, not ABSENT — it never looks inside the folder, and its
    /// absence pass is guarded on <c>IsIncluded = 1</c>, so the exclusion it wrote a moment earlier
    /// takes the row out of reach. The delta only agrees because the two passes are in that same
    /// order; put the subtree pass after, and the row comes out <c>IsPresent = 0</c> on the short
    /// road and <c>IsPresent = 1</c> on the long one.</para>
    /// </summary>
    [Fact]
    public async Task A_file_deleted_from_a_folder_that_goes_hidden_in_the_same_delta_converges()
    {
        var before = SubtreeWorld();
        var after = Replace(before, 140, i => i with
            {
                Attributes = FileAttributes.Directory | FileAttributes.Hidden,
            })
            .Where(i => i.Frn != 205)
            .ToList();

        await AssertConvergesAsync(before, after,
        [
            Change(after, 140, UsnReason.BasicInfoChange | UsnReason.Close),
            Change(before, 205, UsnReason.FileDelete | UsnReason.Close),
        ],
        extra: async db =>
        {
            var deleted = await db.Files.SingleAsync(f => f.UsnFileRef == 205);
            deleted.IsIncluded.Should().BeFalse();
            deleted.IsPresent.Should().BeTrue(
                "a scan never looked inside that folder, so it has nothing to say about the file being gone " +
                "— and the delta must not say it either");
        });
    }

    /// <summary>
    /// A replayed delta must write nothing — that is the property "the cursor is written last"
    /// rests on, and the subtree pass is the first thing in this class that touches rows no record
    /// names, so it is the first that could quietly re-write them for ever.
    ///
    /// <para><b>Asserted in STATEMENTS as well as in rows</b>, because "writes nothing" was not true
    /// of the index half and the snapshot could not tell: the pass pruned the subtree from the FTS
    /// unconditionally, so a DELETE and an INSERT over the whole subtree ran on every replay and
    /// left exactly the rows that were already there. And the case that matters is not the crash
    /// replay, it is the RECURRING one — a folder that stays excluded turns up in every tick that
    /// writes anything inside it, which on a busy folder is every 30 seconds.</para>
    ///
    /// <para>The REAL <see cref="FileSearchIndex"/>, for the reason the cost test gives: with the
    /// fake, the half of this claim that is about the index is not observed at all.</para>
    /// </summary>
    [Fact]
    public async Task Replaying_a_subtree_exclusion_writes_nothing_the_second_time()
    {
        var connection = new CountingSqliteConnection("Data Source=:memory:");
        using var harness = new SqliteInMemoryContext(connection: connection);
        using (var create = harness.CreateContext())
        {
            SqliteFts.Create(create);
        }

        var before = SubtreeWorld();
        var reader = ReaderFor(before);
        var volumeId = await SeedAndScanAsync(harness, reader, before, realSearchIndex: true);

        var after = Replace(before, 140, i => i with
        {
            Attributes = FileAttributes.Directory | FileAttributes.Hidden,
        });
        reader.Script([Change(after, 140, UsnReason.BasicInfoChange | UsnReason.Close)], nextUsn: 900);

        int firstCost;
        await using (var ctx = harness.CreateContext())
        {
            connection.Reset();
            var first = await BuildApplier(ctx, reader, MetadataFor(after), new FileSearchIndex(ctx))
                .SyncVolumeAsync(volumeId, default);
            first.Excluded.Should().Be(2, "every row under the folder had to learn the cause");
            firstCost = connection.Statements;
        }

        var once = await SnapshotAsync(harness);
        var indexOnce = SqliteFts.Rows(harness);
        indexOnce.Should().NotContain(r => r.Path.StartsWith(@"Photos\Cache"),
            "the folder left the perimeter, so nothing under it may still answer a search");

        // Rewind the cursor by hand: the same delta is offered again, exactly as it would be after
        // a crash between the last write and the checkpoint.
        await using (var ctx = harness.CreateContext())
        {
            var volume = await ctx.Volumes.SingleAsync();
            volume.LastUsn = 500;
            await ctx.SaveChangesAsync();
        }

        int replayCost;
        await using (var ctx = harness.CreateContext())
        {
            connection.Reset();
            var again = await BuildApplier(ctx, reader, MetadataFor(after), new FileSearchIndex(ctx))
                .SyncVolumeAsync(volumeId, default);
            again.Excluded.Should().Be(0, "the guard is the cause's own flag: there is nothing left to write");
            replayCost = connection.Statements;
        }

        (await SnapshotAsync(harness)).Should().BeEquivalentTo(once);
        SqliteFts.Rows(harness).Should().Equal(indexOnce);

        // The two runs execute the same code over the same delta; the only difference the replay is
        // allowed to have is the pair of statements SyncDirectoriesAsync issues per chunk of
        // directories — one chunk here — which the guard now skips because no row moved.
        replayCost.Should().Be(firstCost - 2,
            "a subtree whose rows did not move must not have its index rebuilt again");
    }

    /// <summary>
    /// The measure the shape of this pass exists for, in STATEMENTS and not milliseconds: the same
    /// delta, the same excluded folder, ten times the rows behind it — and the same number of
    /// statements. It costs one SELECT of the subtree's directory ids, one UPDATE per cause per
    /// chunk of them, and one pair of statements per chunk for the index. Not one per file, which
    /// is what the per-file <c>RemoveAsync</c> loop the rest of this class uses would have cost.
    ///
    /// <para>Measured with the REAL <c>FileSearchIndex</c> on purpose: with the fake, the half of
    /// the claim that is about the index would not be observed at all, and a per-file loop there
    /// would leave this test green.</para>
    ///
    /// <para><b>What it does NOT say</b>, spelled out because a comparison of two runs is easy to
    /// over-read: it catches a pass that has DEGRADED to per-file, and nothing else. Both numbers
    /// move together, so deleting a statement that runs once — the index sync, say — subtracts the
    /// same amount from each and leaves this green. That the index is touched at all is the
    /// business of <see cref="An_excluded_subtree_leaves_the_search_index_and_takes_nothing_else_with_it"/>,
    /// and that it is NOT touched for nothing is
    /// <see cref="Replaying_a_subtree_exclusion_writes_nothing_the_second_time"/>.</para>
    /// </summary>
    [Fact]
    public async Task The_subtree_pass_costs_the_same_whatever_the_number_of_files_behind_it()
    {
        var few = await SubtreeExclusionCostAsync(files: 5);
        var many = await SubtreeExclusionCostAsync(files: 50);

        many.Should().Be(few,
            "the rows are addressed through their DIRECTORIES, in SQL, and never named one by one");
    }

    private static async Task<int> SubtreeExclusionCostAsync(int files)
    {
        var connection = new CountingSqliteConnection("Data Source=:memory:");
        using var harness = new SqliteInMemoryContext(connection: connection);

        // EnsureCreated builds the EF tables and not the FTS5 virtual one, and half of what this
        // test measures happens inside it.
        using (var create = harness.CreateContext())
        {
            SqliteFts.Create(create);
        }

        List<Item> before =
        [
            Dir(100, RootFrn, "Photos"),
            Dir(140, 100, @"Photos\Cache"),
            Dir(150, 140, @"Photos\Cache\Sub"),
            .. Enumerable.Range(0, files)
                .Select(i => File(1000UL + (ulong)i, 150, $@"Photos\Cache\Sub\f{i:D4}.jpg", 10)),
        ];

        var reader = ReaderFor(before);
        var volumeId = await SeedAndScanAsync(harness, reader, before);

        var after = Replace(before, 140, i => i with
        {
            Attributes = FileAttributes.Directory | FileAttributes.Hidden,
        });
        reader.Script([Change(after, 140, UsnReason.BasicInfoChange | UsnReason.Close)], nextUsn: 900);

        await using var ctx = harness.CreateContext();
        var applier = BuildApplier(ctx, reader, MetadataFor(after), new FileSearchIndex(ctx));

        // Counted from here: one delta, nothing else.
        connection.Reset();
        var result = await applier.SyncVolumeAsync(volumeId, default);
        result.Excluded.Should().Be(files, "every row behind the folder is out, however many there are");

        return connection.Statements;
    }

    // ── the cursor ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_cursor_advances_only_after_the_delta_is_applied()
    {
        using var harness = new SqliteInMemoryContext();
        var reader = ReaderFor(StartingWorld());
        var volumeId = await SeedAndScanAsync(harness, reader, StartingWorld());

        var after = new List<Item>(StartingWorld()) { File(203, 100, @"Photos\new.jpg", 44) };
        reader.Script([Change(after, 203, UsnReason.FileCreate | UsnReason.Close)], nextUsn: 900);

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
        reader.Script(
        [
            Change(StartingWorld(), 202, UsnReason.FileDelete | UsnReason.Close),
            Change(after, 203, UsnReason.FileCreate | UsnReason.Close),
        ], nextUsn: 900);

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

    /// <param name="between">
    /// Run on BOTH databases after the starting scan and before the second step. The world is not
    /// the only thing that can change between two scans — a SETTING can, and a case about the two
    /// perimeter rules meeting on one folder needs exactly that: the path rule has to be NEW in the
    /// delta, or a cause the starting scan already wrote would hide a cause the delta dropped.
    /// </param>
    private static async Task AssertConvergesAsync(
        List<Item> before,
        List<Item> after,
        List<UsnChangeRecord> changes,
        Func<FileTracertDbContext, Task>? extra = null,
        Func<SqliteInMemoryContext, Task>? between = null)
    {
        // Long road: full scan of the starting world, then a full re-scan of the changed one.
        using var viaScan = new SqliteInMemoryContext();
        var scanReader = ReaderFor(before);
        var scanVolumeId = await SeedAndScanAsync(viaScan, scanReader, before);
        if (between is not null)
        {
            await between(viaScan);
        }

        var rescanReader = ReaderFor(after);
        await ScanAsync(viaScan, scanVolumeId, rescanReader, MetadataFor(after));

        // Short road: the same starting scan, then the delta that describes the same change.
        using var viaDelta = new SqliteInMemoryContext();
        var deltaReader = ReaderFor(before);
        var deltaVolumeId = await SeedAndScanAsync(viaDelta, deltaReader, before);
        if (between is not null)
        {
            await between(viaDelta);
        }

        deltaReader.Script(changes, nextUsn: 900);

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

    /// <param name="realSearchIndex">
    /// Populate the REAL FTS5 table from the starting scan, for the cases whose subject is the
    /// index. Off by default because most cases never look at it and the virtual table then does
    /// not even have to exist; on, the caller must have run <see cref="SqliteFts.Create"/> first.
    /// Without it the index is empty when the delta runs, and every assertion about what the delta
    /// took OUT of it is vacuously true.
    /// </param>
    private static async Task<int> SeedAndScanAsync(
        SqliteInMemoryContext harness, ScriptedUsnReader reader, List<Item> world,
        bool realSearchIndex = false)
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

        await ScanAsync(harness, volumeId, reader, MetadataFor(world), realSearchIndex);
        return volumeId;
    }

    private static async Task ScanAsync(
        SqliteInMemoryContext harness, int volumeId, ScriptedUsnReader reader, FakeFileMetadataReader metadata,
        bool realSearchIndex = false)
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
            realSearchIndex ? new FileSearchIndex(ctx) : new FakeFileSearchIndex(),
            new FakeNotificationPublisher(),
            new ScanStatusTracker(TestProjection.Realtime(), TimeProvider.System),
            NullLogger<ScanService>.Instance);

        await scan.ScanVolumeAsync(volumeId, CancellationToken.None);
    }

    /// <param name="searchIndex">The real <see cref="FileSearchIndex"/> where what the pass does to
    /// the index is part of what is being measured; the fake everywhere else.</param>
    private static UsnDeltaApplier BuildApplier(
        FileTracertDbContext ctx,
        ScriptedUsnReader reader,
        FakeFileMetadataReader metadata,
        IFileSearchIndex? searchIndex = null) =>
        new(ctx,
            new FakeVolumeProbe(Probed),
            reader,
            metadata,
            new BulkIndexWriter(ctx),
            new DirectoryMerger(ctx, new BulkIndexWriter(ctx), NullLogger<DirectoryMerger>.Instance),
            searchIndex ?? new FakeFileSearchIndex(),
            NullLogger<UsnDeltaApplier>.Instance);

    private static List<Item> Replace(List<Item> world, ulong frn, Func<Item, Item> change) =>
        world.Select(i => i.Frn == frn ? change(i) : i).ToList();
}
