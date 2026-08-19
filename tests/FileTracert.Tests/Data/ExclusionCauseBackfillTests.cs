using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
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
