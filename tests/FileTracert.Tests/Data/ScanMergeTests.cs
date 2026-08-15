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

            (await writer.MarkAbsentFilesAsync(fx.VolumeId, scanStart, CancellationToken.None)).Should().Be(1);
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

            await writer.MarkAbsentFilesAsync(fx.VolumeId, T0.AddHours(1), CancellationToken.None);
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
