using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

/// <summary>
/// The scan merge, against real SQLite: a re-scan must update the rows it finds
/// again, insert the new ones and mark the missing ones absent — never truncate
/// (which is what used to destroy identities and the pending overlay).
/// </summary>
public sealed class ScanMergeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(int VolumeId, int RootDirId, int SubDirId);

    private static async Task<Fixture> SeedAsync(SqliteInMemoryContext harness)
    {
        await using var ctx = harness.CreateContext();
        var volume = new Volume
        {
            VolumeGuid = Guid.NewGuid().ToString(), FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
        };
        ctx.Volumes.Add(volume);
        await ctx.SaveChangesAsync();

        var root = new DirectoryNode { VolumeId = volume.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
        ctx.Directories.Add(root);
        await ctx.SaveChangesAsync();

        var sub = new DirectoryNode { VolumeId = volume.Id, ParentId = root.Id, Name = "Sub", MaterializedPath = "Sub", IsMaterialized = true };
        ctx.Directories.Add(sub);
        await ctx.SaveChangesAsync();

        return new Fixture(volume.Id, root.Id, sub.Id);
    }

    private static FileEntry Scanned(
        int volumeId, int dirId, string name, long size = 10, long? frn = null, DateTime? indexedUtc = null) => new()
    {
        VolumeId = volumeId,
        DirectoryId = dirId,
        Name = name,
        Extension = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant() : "",
        Category = FileCategory.Image,
        SizeBytes = size,
        FileCreatedUtc = T0,
        FileModifiedUtc = T0,
        Attributes = FileAttributes.Normal,
        UsnFileRef = frn,
        IsIncluded = true,
        IsPresent = true,
        LastIndexedUtc = indexedUtc ?? T0,
    };

    [Fact]
    public async Task Merge_inserts_files_that_are_not_in_the_catalog_yet()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        await using var ctx = harness.CreateContext();
        var writer = new BulkIndexWriter(ctx);

        var result = await writer.MergeScannedFilesAsync(
            fx.VolumeId,
            [Scanned(fx.VolumeId, fx.RootDirId, "a.jpg"), Scanned(fx.VolumeId, fx.SubDirId, "b.jpg")],
            T0, CancellationToken.None);

        result.Inserted.Should().Be(2);
        result.Updated.Should().Be(0);
        result.AffectedFileIds.Should().HaveCount(2);

        await using var read = harness.CreateContext();
        (await read.Files.Select(f => f.Name).ToListAsync()).Should().BeEquivalentTo("a.jpg", "b.jpg");
        result.AffectedFileIds.Should().BeEquivalentTo(await read.Files.Select(f => f.Id).ToListAsync());
    }

    [Fact]
    public async Task Merge_keeps_the_row_identity_and_the_pending_overlay_of_a_file_it_finds_again()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        int fileId;
        await using (var ctx = harness.CreateContext())
        {
            var writer = new BulkIndexWriter(ctx);
            await writer.MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "a.jpg")], T0, CancellationToken.None);

            var file = await ctx.Files.SingleAsync();
            fileId = file.Id;
            file.PendingName = "renamed.jpg";
            file.PendingState = EntityPendingState.PendingRename;
            file.PendingJobId = 42;
            file.Hash = "deadbeef";
            file.QuickHash = "cafe";
            await ctx.SaveChangesAsync();
        }

        var later = T0.AddHours(1);
        await using (var ctx = harness.CreateContext())
        {
            var writer = new BulkIndexWriter(ctx);
            var result = await writer.MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "a.jpg", size: 999)], later, CancellationToken.None);

            result.Inserted.Should().Be(0);
            result.Updated.Should().Be(1);
        }

        await using var read = harness.CreateContext();
        var merged = await read.Files.SingleAsync();
        merged.Id.Should().Be(fileId);                                  // identity survives (OperationJobItems.FileId)
        merged.PendingName.Should().Be("renamed.jpg");                  // overlay untouched
        merged.PendingState.Should().Be(EntityPendingState.PendingRename);
        merged.PendingJobId.Should().Be(42);
        merged.Hash.Should().Be("deadbeef");                            // hashes are not re-derived by a scan
        merged.QuickHash.Should().Be("cafe");
        merged.SizeBytes.Should().Be(999);                              // physical fields do get refreshed
        merged.LastIndexedUtc.Should().Be(later);
    }

    [Fact]
    public async Task Merge_matches_the_path_case_insensitively()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        await using (var ctx = harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.SubDirId, "Foto.JPG")], T0, CancellationToken.None);
        }

        await using (var ctx = harness.CreateContext())
        {
            var result = await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.SubDirId, "foto.jpg", size: 77)], T0.AddHours(1), CancellationToken.None);
            result.Inserted.Should().Be(0);
            result.Updated.Should().Be(1);
        }

        await using var read = harness.CreateContext();
        var only = await read.Files.SingleAsync();
        only.SizeBytes.Should().Be(77);
        only.Name.Should().Be("foto.jpg"); // the on-disk spelling wins
    }

    [Fact]
    public async Task Merge_matches_by_usn_file_reference_when_the_file_was_renamed_outside_the_app()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        int fileId;
        await using (var ctx = harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "old.jpg", frn: 5150)], T0, CancellationToken.None);
            fileId = await ctx.Files.Select(f => f.Id).SingleAsync();
        }

        await using (var ctx = harness.CreateContext())
        {
            // Same FRN, different name AND different directory: one row moved, not two rows.
            var result = await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.SubDirId, "new.jpg", frn: 5150)], T0.AddHours(1), CancellationToken.None);
            result.Inserted.Should().Be(0);
            result.Updated.Should().Be(1);
        }

        await using var read = harness.CreateContext();
        var only = await read.Files.SingleAsync();
        only.Id.Should().Be(fileId);
        only.Name.Should().Be("new.jpg");
        only.DirectoryId.Should().Be(fx.SubDirId);
    }

    [Fact]
    public async Task A_rename_plus_a_recreated_file_do_not_collapse_onto_the_same_row()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        int originalId;
        await using (var ctx = harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "a.jpg", frn: 10)], T0, CancellationToken.None);
            originalId = await ctx.Files.Select(f => f.Id).SingleAsync();
        }

        await using (var ctx = harness.CreateContext())
        {
            // a.jpg was renamed to b.jpg (FRN 10 follows it) and a brand new a.jpg took its
            // place (FRN 11). The new file matches the old row by path — it must not steal it.
            var result = await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId,
                [Scanned(fx.VolumeId, fx.RootDirId, "b.jpg", frn: 10), Scanned(fx.VolumeId, fx.RootDirId, "a.jpg", frn: 11)],
                T0.AddHours(1), CancellationToken.None);

            result.Updated.Should().Be(1);
            result.Inserted.Should().Be(1);
        }

        await using var read = harness.CreateContext();
        var rows = await read.Files.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(f => f.Name == "b.jpg").Id.Should().Be(originalId);
        rows.Single(f => f.Name == "a.jpg").UsnFileRef.Should().Be(11);
    }

    [Fact]
    public async Task Marking_absent_only_touches_included_rows_the_scan_did_not_see()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        await using (var ctx = harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId,
                [Scanned(fx.VolumeId, fx.RootDirId, "stays.jpg"), Scanned(fx.VolumeId, fx.RootDirId, "goes.jpg")],
                T0, CancellationToken.None);

            // A file the filter excludes is never re-indexed by a scan; it is still on disk
            // and must NOT be swept away by the absent pass.
            var excluded = Scanned(fx.VolumeId, fx.RootDirId, "excluded.bin");
            excluded.IsIncluded = false;
            ctx.Files.Add(excluded);
            await ctx.SaveChangesAsync();
        }

        var scanStart = T0.AddHours(1);
        await using (var ctx = harness.CreateContext())
        {
            var writer = new BulkIndexWriter(ctx);
            await writer.MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "stays.jpg")], scanStart.AddMinutes(1), CancellationToken.None);

            (await writer.ReconcileUnseenFilesAsync(fx.VolumeId, scanStart, [], CancellationToken.None))
                .Should().Be(new ScanClosureResult(Excluded: 0, Absent: 1));
        }

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "stays.jpg")).IsPresent.Should().BeTrue();
        (await read.Files.SingleAsync(f => f.Name == "goes.jpg")).IsPresent.Should().BeFalse();
        (await read.Files.SingleAsync(f => f.Name == "excluded.bin")).IsPresent.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_that_reappears_gets_its_original_row_back()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        int fileId;
        await using (var ctx = harness.CreateContext())
        {
            var writer = new BulkIndexWriter(ctx);
            await writer.MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "blink.jpg")], T0, CancellationToken.None);
            fileId = await ctx.Files.Select(f => f.Id).SingleAsync();

            await writer.ReconcileUnseenFilesAsync(fx.VolumeId, T0.AddHours(1), [], CancellationToken.None);
        }

        await using (var ctx = harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "blink.jpg")], T0.AddHours(2), CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        var back = await read.Files.SingleAsync();
        back.Id.Should().Be(fileId);
        back.IsPresent.Should().BeTrue();
    }

    // ── step 11g: what the scan skipped is excluded, what it missed is absent ──

    [Fact]
    public async Task A_row_inside_a_skipped_directory_is_excluded_and_keeps_its_presence()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        await using (var ctx = harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId,
                [
                    Scanned(fx.VolumeId, fx.RootDirId, "seen.jpg"),
                    Scanned(fx.VolumeId, fx.SubDirId, "skipped.jpg"),
                    Scanned(fx.VolumeId, fx.RootDirId, "gone.jpg"),
                ],
                T0, CancellationToken.None);
        }

        // The next scan re-indexes only "seen.jpg": "Sub" is outside the perimeter it walked, and
        // "gone.jpg" is inside it but no longer on disk.
        var scanStart = T0.AddHours(1);
        await using (var ctx = harness.CreateContext())
        {
            var writer = new BulkIndexWriter(ctx);
            await writer.MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "seen.jpg")], scanStart.AddMinutes(1),
                CancellationToken.None);

            var closure = await writer.ReconcileUnseenFilesAsync(
                fx.VolumeId, scanStart, [new SkippedScanArea(fx.SubDirId, FileName: null, ScanSkipCause.FilteredOut)], CancellationToken.None);

            closure.Should().Be(new ScanClosureResult(Excluded: 1, Absent: 1));
        }

        await using var read = harness.CreateContext();
        var skipped = await read.Files.SingleAsync(f => f.Name == "skipped.jpg");
        skipped.IsIncluded.Should().BeFalse();
        skipped.IsPresent.Should().BeTrue("the scan never looked there, so it says nothing about the disk");

        var gone = await read.Files.SingleAsync(f => f.Name == "gone.jpg");
        gone.IsPresent.Should().BeFalse();
        gone.IsIncluded.Should().BeTrue("absence is not a filter decision");

        var seen = await read.Files.SingleAsync(f => f.Name == "seen.jpg");
        seen.IsIncluded.Should().BeTrue();
        seen.IsPresent.Should().BeTrue();
    }

    [Fact]
    public async Task A_single_skipped_file_is_excluded_without_touching_its_neighbours()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        await using (var ctx = harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId,
                [Scanned(fx.VolumeId, fx.RootDirId, "Hidden.jpg"), Scanned(fx.VolumeId, fx.RootDirId, "next.jpg")],
                T0, CancellationToken.None);
        }

        var scanStart = T0.AddHours(1);
        await using (var ctx = harness.CreateContext())
        {
            var writer = new BulkIndexWriter(ctx);
            await writer.MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "next.jpg")], scanStart.AddMinutes(1),
                CancellationToken.None);

            // Spelled in the other case on purpose: Windows does not distinguish it, and SQLite's
            // default BINARY collation does.
            var closure = await writer.ReconcileUnseenFilesAsync(
                fx.VolumeId, scanStart, [new SkippedScanArea(fx.RootDirId, "hidden.JPG", ScanSkipCause.FilteredOut)], CancellationToken.None);

            closure.Should().Be(new ScanClosureResult(Excluded: 1, Absent: 0));
        }

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "Hidden.jpg")).IsIncluded.Should().BeFalse();
        (await read.Files.SingleAsync(f => f.Name == "Hidden.jpg")).IsPresent.Should().BeTrue();
        (await read.Files.SingleAsync(f => f.Name == "next.jpg")).IsIncluded.Should().BeTrue();
    }

    [Fact]
    public async Task Merge_records_that_a_row_it_saw_again_is_included()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        await using (var ctx = harness.CreateContext())
        {
            // A row the perimeter excluded on an earlier scan — nothing in Setup can un-exclude it
            // when the reason was an attribute on disk, so the scan that sees it again must.
            var excluded = Scanned(fx.VolumeId, fx.RootDirId, "back.jpg");
            excluded.IsIncluded = false;
            excluded.IsPresent = true;
            ctx.Files.Add(excluded);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = harness.CreateContext())
        {
            var result = await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "back.jpg")], T0.AddHours(1),
                CancellationToken.None);
            result.Updated.Should().Be(1, "the row is matched, not duplicated");
        }

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync()).IsIncluded.Should().BeTrue();
    }

    /// <summary>
    /// The cost of closing a scan follows the areas the pipeline skipped, never the rows behind
    /// them — and when it skipped nothing (the ordinary case: the perimeter has not moved) the
    /// pass is the single UPDATE it has always been.
    ///
    /// <para>What this does NOT prove, said out loud so nobody reads more into the number: the
    /// staging fill re-executes one prepared INSERT per AREA, and the counter cannot see those
    /// (it counts commands, and that one is built once). The area count is the pipeline's input,
    /// not a function of the catalog, and what keeps it small is upstream — <c>ScanService</c>
    /// only records a skipped file whose type the allow-list would have admitted, so the hidden
    /// files nobody indexes never become areas.</para>
    /// </summary>
    [Fact]
    public async Task Closing_a_scan_costs_the_same_whatever_the_number_of_rows_behind_the_skipped_areas()
    {
        var (fewStatements, fewExcluded) = await ClosureCostAsync(rowsInSkippedDirectory: 50);
        var (manyStatements, manyExcluded) = await ClosureCostAsync(rowsInSkippedDirectory: 500);

        fewExcluded.Should().Be(50);
        manyExcluded.Should().Be(500);
        manyStatements.Should().Be(fewStatements,
            "the pass is set-based: ten times the rows, the same statements");

        // And with nothing skipped it is exactly one statement — the absence UPDATE, unchanged.
        var (baseline, _) = await ClosureCostAsync(rowsInSkippedDirectory: 500, skipTheDirectory: false);
        baseline.Should().Be(1);
        fewStatements.Should().Be(6,
            "one skipped area costs the staging table, its index, the DELETE that empties it, one " +
            "INSERT, the exclusion UPDATE and the absence UPDATE — and nothing per row");

        // Step 11h: a second CAUSE among the areas costs one more UPDATE, and only one — the flag
        // each cause writes is different, so they cannot share a statement, but the cost still
        // follows the number of causes (two) and never the rows.
        var (twoCauses, _) = await ClosureCostAsync(rowsInSkippedDirectory: 500, secondCause: true);
        twoCauses.Should().Be(7);
    }

    private static async Task<(int Statements, int Excluded)> ClosureCostAsync(
        int rowsInSkippedDirectory, bool skipTheDirectory = true, bool secondCause = false)
    {
        var connection = new CountingSqliteConnection("Data Source=:memory:");
        using var harness = new SqliteInMemoryContext(connection: connection);
        var fx = await SeedAsync(harness);

        await using (var ctx = harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId,
                [.. Enumerable.Range(0, rowsInSkippedDirectory)
                    .Select(i => Scanned(fx.VolumeId, fx.SubDirId, $"f{i:D4}.jpg"))],
                T0, CancellationToken.None);
        }

        await using var run = harness.CreateContext();
        var writer = new BulkIndexWriter(run);

        // Counted from here: the closure pass only, not the arrange.
        SkippedScanArea[] areas = skipTheDirectory
            ? secondCause
                ? [new SkippedScanArea(fx.SubDirId, null, ScanSkipCause.FilteredOut),
                   new SkippedScanArea(fx.RootDirId, null, ScanSkipCause.InactiveRoot)]
                : [new SkippedScanArea(fx.SubDirId, null, ScanSkipCause.FilteredOut)]
            : [];

        connection.Reset();
        var closure = await writer.ReconcileUnseenFilesAsync(
            fx.VolumeId, T0.AddHours(1), areas, CancellationToken.None);

        return (connection.Statements, closure.Excluded);
    }

    [Fact]
    public async Task Merge_does_not_resurrect_a_file_the_filter_excluded()
    {
        using var harness = new SqliteInMemoryContext();
        var fx = await SeedAsync(harness);

        await using (var ctx = harness.CreateContext())
        {
            var excluded = Scanned(fx.VolumeId, fx.RootDirId, "clip.avi");
            excluded.IsIncluded = false;
            ctx.Files.Add(excluded);
            await ctx.SaveChangesAsync();
        }

        // The scan pipeline filters it out, so the merge simply never sees it.
        await using (var ctx = harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                fx.VolumeId, [Scanned(fx.VolumeId, fx.RootDirId, "keep.jpg")], T0.AddHours(1), CancellationToken.None);
        }

        await using var read = harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "clip.avi")).IsIncluded.Should().BeFalse();
        (await read.Files.CountAsync()).Should().Be(2);
    }
}
