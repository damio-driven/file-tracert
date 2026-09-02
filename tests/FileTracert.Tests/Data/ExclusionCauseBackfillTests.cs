using FileTracert.Business.Filtering;
using FileTracert.Business.Setup;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using FileTracert.Data.Search;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FileTracert.Tests.Data;

/// <summary>
/// What the step 11h migration does to a database written by the OLD behaviour, where
/// <c>IsIncluded = 0</c> was a bit with no memory of why.
///
/// <para>Run against the real migration pipeline — migrate to the version before, write rows the
/// way that version could, then migrate forward — because the whole question is what happens to
/// data that already exists, and a model built by <c>EnsureCreated</c> has no history to have got
/// wrong.</para>
/// </summary>
public sealed class ExclusionCauseBackfillTests : IDisposable
{
    private const string PreviousMigration = "CatalogCountCoveringIndexes";
    private const string MigrationBeforeThePathCause = "CatalogVisibilityIncludesProjectedCopies";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ExclusionCauseBackfillTests()
    {
        SQLitePCL.Batteries.Init();
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private FileTracertDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FileTracertDbContext>().UseSqlite(_connection).Options);

    /// <summary>
    /// The honest answer for a row whose cause is unknowable: the one cause reconciliation never
    /// undoes. Anything else and the next filter change would walk the content of a hidden folder
    /// straight back into the Catalog — silently, which is the failure mode this step exists to
    /// remove.
    /// </summary>
    [Fact]
    public async Task Rows_excluded_by_the_old_behaviour_are_stamped_with_the_cause_nobody_can_undo()
    {
        await MigrateToAsync(PreviousMigration);
        await SeedLegacyRowsAsync();

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using var read = CreateContext();
        var excluded = await read.Files.SingleAsync(f => f.Name == "old.txt");
        excluded.ExcludedByScan.Should().BeTrue(
            "nothing in the row says why it is out, and only the pessimistic answer is safe");
        excluded.ExcludedByType.Should().BeFalse();
        excluded.ExcludedByRoot.Should().BeFalse();

        var included = await read.Files.SingleAsync(f => f.Name == "old.jpg");
        included.IsIncluded.Should().BeTrue();
        included.ExcludedByScan.Should().BeFalse("an included row carries no cause at all");
    }

    /// <summary>
    /// The point of the pessimistic stamp: a reconciliation over a legacy row must NOT walk it back
    /// into the Catalog on its own, however wide the filter gets. Run through the real
    /// <see cref="FilterReconciler"/>, because "the flag is set" and "the reconciler honours it"
    /// are two different claims.
    /// </summary>
    [Fact]
    public async Task A_backfilled_row_does_not_come_back_on_a_widened_filter_alone()
    {
        await MigrateToAsync(PreviousMigration);
        await SeedLegacyRowsAsync();

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = CreateContext())
        {
            var root = new WatchedRoot { VolumeId = 1, RelativePath = string.Empty, IsActive = true };
            db.WatchedRoots.Add(root);
            await db.SaveChangesAsync();

            // The widest filter there is: every type allowed.
            await new FilterReconciler(db, new FileSearchIndex(db)).ReconcileRootAsync(
                root, new EffectiveFilter(new HashSet<string>(), []), CancellationToken.None);
        }

        await using var read = CreateContext();
        (await read.Files.SingleAsync(f => f.Name == "old.txt")).IsIncluded
            .Should().BeFalse("a legacy row whose cause is unknown stays out until a scan says otherwise");
    }

    /// <summary>
    /// …and the backfilled row is not stuck: a scan that sees the file again clears every cause,
    /// which is exactly the "let the first scan correct it" the pessimistic stamp relies on.
    /// </summary>
    [Fact]
    public async Task A_backfilled_row_comes_back_when_a_scan_sees_the_file_again()
    {
        await MigrateToAsync(PreviousMigration);
        await SeedLegacyRowsAsync();

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = CreateContext())
        {
            var row = await db.Files.SingleAsync(f => f.Name == "old.txt");
            await new BulkIndexWriter(db).MergeScannedFilesAsync(
                row.VolumeId,
                [new FileEntry
                {
                    VolumeId = row.VolumeId, DirectoryId = row.DirectoryId, Name = "old.txt",
                    Extension = "txt", Category = FileCategory.Document,
                    SizeBytes = 1, IsIncluded = true, IsPresent = true,
                    LastIndexedUtc = DateTime.UtcNow,
                }],
                DateTime.UtcNow,
                CancellationToken.None);
        }

        await using var read = CreateContext();
        var repaired = await read.Files.SingleAsync(f => f.Name == "old.txt");
        repaired.IsIncluded.Should().BeTrue();
        repaired.ExcludedByScan.Should().BeFalse("the merge IS the filter's decision (§4)");
    }

    /// <summary>
    /// Step 16 splits the path half out of <c>ExcludedByScan</c>, and the migration deliberately
    /// carries NO backfill: a row already excluded stays excluded for the cause nobody can undo.
    /// The alternative — reading <c>MaterializedPath</c> and re-attributing rows to
    /// <c>ExcludedByPath</c> — would let the very next widening of <c>ExcludedPaths</c> walk the
    /// content of a hidden folder back into the Catalog, silently, for every row the old code had
    /// lumped together.
    /// </summary>
    [Fact]
    public async Task The_path_cause_arrives_empty_and_takes_nothing_away_from_the_attribute_cause()
    {
        await MigrateToAsync(MigrationBeforeThePathCause);
        await SeedLegacyRowsAsync();

        // Written the way the PREVIOUS version wrote a path-excluded row: one flag for both facts.
        await using (var legacy = CreateContext())
        {
            await legacy.Database.ExecuteSqlRawAsync(
                "UPDATE Files SET ExcludedByScan = 1, IsIncluded = 0 WHERE Name = 'old.txt';");
        }

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using var read = CreateContext();
        var legacyRow = await read.Files.SingleAsync(f => f.Name == "old.txt");
        legacyRow.ExcludedByScan.Should().BeTrue("the migration does not guess which of the two facts it was");
        legacyRow.ExcludedByPath.Should().BeFalse("the new column starts empty on every existing row");
        legacyRow.IsIncluded.Should().BeFalse();

        var included = await read.Files.SingleAsync(f => f.Name == "old.jpg");
        included.ExcludedByPath.Should().BeFalse();
        included.IsIncluded.Should().BeTrue("an included row is left alone by an additive column");
    }

    /// <summary>
    /// The sequence the pessimistic stamp exists for, end to end — and the one the assertion above
    /// does not reach, because "a column with <c>defaultValue: false</c> is false" is close to an
    /// echo of the migration itself.
    ///
    /// <para>A row excluded by the OLD code under <c>AppData</c> carries one flag for two possible
    /// facts: the segment, or a Hidden folder. Guessing "segment" would have been the convenient
    /// answer and the dangerous one — the user then drops <c>AppData</c> and the content of a hidden
    /// folder comes back into the Catalog, silently. So it is held, and the row beside it, indexed
    /// after the split, does exactly what the user asked: out with the segment, back without it.
    /// That contrast is what makes this a test of the backfill and not of the reconciler refusing to
    /// touch <c>ExcludedByScan</c> in general.</para>
    /// </summary>
    [Fact]
    public async Task A_legacy_row_is_not_re_admitted_by_dropping_the_segment_that_might_have_excluded_it()
    {
        await MigrateToAsync(MigrationBeforeThePathCause);
        await SeedLegacyRowsAsync();

        await using (var legacy = CreateContext())
        {
            // A folder on what will become the excluded list, holding two rows: one the old code
            // had already excluded (cause forgotten), one it had not.
            await legacy.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Directories (VolumeId, ParentId, Name, MaterializedPath, IsMaterialized,
                                         IsPresent, PendingState, CreatedUtc, UpdatedUtc)
                VALUES (1, 1, 'AppData', 'AppData', 1, 1, 'None',
                        '2026-08-19 00:00:00', '2026-08-19 00:00:00');

                INSERT INTO Files (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
                                   CreatedUtc, ModifiedUtc, Attributes, IsIncluded, ExcludedByType,
                                   ExcludedByRoot, ExcludedByScan, IsPresent,
                                   LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
                VALUES (1, 2, 'legacy.txt', 'txt', 'Document', 1,
                        '2026-08-19 00:00:00', '2026-08-19 00:00:00', 0, 0, 0, 0, 1, 1,
                        '2026-08-19 00:00:00', 'None', '2026-08-19 00:00:00', '2026-08-19 00:00:00'),
                       (1, 2, 'fresh.txt', 'txt', 'Document', 1,
                        '2026-08-19 00:00:00', '2026-08-19 00:00:00', 0, 1, 0, 0, 0, 1,
                        '2026-08-19 00:00:00', 'None', '2026-08-19 00:00:00', '2026-08-19 00:00:00');
                """);
        }

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        var root = 0;
        await using (var db = CreateContext())
        {
            var watched = new WatchedRoot { VolumeId = 1, RelativePath = string.Empty, IsActive = true };
            db.WatchedRoots.Add(watched);
            await db.SaveChangesAsync();
            root = watched.Id;
        }

        await ReconcileAsync(root, @"AppData\");

        await using (var narrowed = CreateContext())
        {
            (await narrowed.Files.SingleAsync(f => f.Name == "fresh.txt")).ExcludedByPath
                .Should().BeTrue("arrange: the segment applies to this folder");
        }

        // The user changes their mind and drops the segment.
        await ReconcileAsync(root);

        await using var read = CreateContext();
        var fresh = await read.Files.SingleAsync(f => f.Name == "fresh.txt");
        fresh.IsIncluded.Should().BeTrue("a row whose only cause was the segment comes back, no scan");

        var stamped = await read.Files.SingleAsync(f => f.Name == "legacy.txt");
        stamped.ExcludedByPath.Should().BeFalse();
        stamped.ExcludedByScan.Should().BeTrue();
        stamped.IsIncluded.Should().BeFalse(
            "its cause was never knowable, so it waits for a scan rather than being guessed back in");
    }

    private async Task ReconcileAsync(int rootId, params string[] excludedSegments)
    {
        await using var db = CreateContext();
        var root = await db.WatchedRoots.SingleAsync(r => r.Id == rootId);
        await new FilterReconciler(db, new FileSearchIndex(db)).ReconcileRootAsync(
            root, new EffectiveFilter(new HashSet<string>(), excludedSegments), CancellationToken.None);
    }

    private async Task MigrateToAsync(string migration)
    {
        await using var db = CreateContext();
        await db.Database.GetService<IMigrator>().MigrateAsync(migration);
    }

    /// <summary>
    /// Raw SQL on purpose: the entity now has columns the target schema does not, so EF cannot
    /// write this row — which is the situation being reproduced.
    /// </summary>
    private async Task SeedLegacyRowsAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Volumes (VolumeGuid, FileSystem, IsRemovable, CapacityBytes, FreeBytesLastKnown,
                                 LastSeenUtc, IsOnline, ScanEngine, Kind, IsCatalogable,
                                 CreatedUtc, UpdatedUtc)
            VALUES ({0}, 'NTFS', 0, 0, 0,
                    '2026-08-19 00:00:00', 1, 'UsnJournal', 'Fixed', 1,
                    '2026-08-19 00:00:00', '2026-08-19 00:00:00');

            INSERT INTO Directories (VolumeId, Name, MaterializedPath, IsMaterialized, IsPresent,
                                     PendingState, CreatedUtc, UpdatedUtc)
            VALUES (1, '', '', 1, 1, 'None', '2026-08-19 00:00:00', '2026-08-19 00:00:00');

            INSERT INTO Files (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
                               CreatedUtc, ModifiedUtc, Attributes, IsIncluded, IsPresent,
                               LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
            VALUES (1, 1, 'old.txt', 'txt', 'Document', 1,
                    '2026-08-19 00:00:00', '2026-08-19 00:00:00', 0, 0, 1,
                    '2026-08-19 00:00:00', 'None', '2026-08-19 00:00:00', '2026-08-19 00:00:00'),
                   (1, 1, 'old.jpg', 'jpg', 'Image', 1,
                    '2026-08-19 00:00:00', '2026-08-19 00:00:00', 0, 1, 1,
                    '2026-08-19 00:00:00', 'None', '2026-08-19 00:00:00', '2026-08-19 00:00:00');
            """,
            [@"\\?\Volume{11111111-1111-1111-1111-111111111111}\"]);
    }
}
