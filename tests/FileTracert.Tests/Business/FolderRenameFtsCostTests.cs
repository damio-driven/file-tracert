using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Business;

/// <summary>
/// E4 — a folder-wide rename re-syncs the search index in batches, not one file at a time.
///
/// <c>UpsertAsync</c> is a DELETE plus an INSERT. Calling it per file meant a folder rename over
/// 50 000 files cost 100 000 statements, and the loop that produced them first materialised the
/// full <c>FileEntry</c> of every one of those files just to read its name. Both are gone: the ids
/// go to <see cref="FileTracert.Contracts.Search.IFileSearchIndex.SyncDirectoriesAsync"/>, which
/// rebuilds the entries with one <c>DELETE</c> + <c>INSERT … SELECT</c> per chunk of DIRECTORIES —
/// so the cost follows the shape of the folder tree, never the number of files inside it.
///
/// The tests run against the REAL <see cref="FileSearchIndex"/> over a real FTS5 table, because the
/// rule that builds the indexed name and path moved INTO that SQL — a fake would prove nothing
/// about the rows that come out.
/// </summary>
public sealed class FolderRenameFtsCostTests : IDisposable
{
    private const int VolId = 1;
    private const int FolderDirId = 10;

    private readonly SqliteInMemoryContext _harness = new();
    private readonly CountingCommandInterceptor _sql = new();

    public FolderRenameFtsCostTests()
    {
        using var setup = _harness.CreateContext();
        setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        SqliteFts.Create(setup);
    }

    public void Dispose() => _harness.Dispose();

    // ── correctness ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_file_of_the_renamed_folder_keeps_an_entry_under_the_new_path()
    {
        Seed(fileCount: 600);
        await RenameFolderAsync("Foto", "Immagini");

        var rows = FtsRows();

        rows.Should().HaveCount(600);
        rows.Should().OnlyContain(r => r.Path.StartsWith(@"Immagini\", StringComparison.Ordinal));
        rows.Should().Contain(r => r.Rowid == 1 && r.Name == "f00000.jpg" && r.Path == @"Immagini\f00000.jpg");
        rows.Should().Contain(r => r.Rowid == 600 && r.Path == @"Immagini\f00599.jpg");
    }

    /// <summary>
    /// §5 / step 9b — the <c>name</c> column is the PROJECTED name,
    /// <c>COALESCE(NULLIF(PendingName, ''), Name)</c>. This path used to spell the physical name by
    /// hand, which made it the only one of the four population paths that disagreed with the rule.
    /// Handing the ids to the index puts them all on the one definition.
    /// </summary>
    [Fact]
    public async Task A_file_with_a_queued_rename_is_indexed_under_its_projected_name()
    {
        Seed(fileCount: 3);
        using (var db = _harness.CreateContext())
        {
            var file = db.Files.Single(f => f.Id == 2);
            file.PendingName = "rinominato.jpg";
            file.PendingState = EntityPendingState.PendingRename;
            db.SaveChanges();
        }

        await RenameFolderAsync("Foto", "Immagini");

        FtsRows().Should().Contain(r => r.Rowid == 2 && r.Name == "rinominato.jpg");
    }

    /// <summary>
    /// The index holds only what is includable (<c>IsIncluded AND IsPresent</c>). The old loop
    /// filtered those out of the LIST and therefore just skipped them, leaving whatever stale entry
    /// they already had pointing at the folder's old path. Re-syncing the id removes it.
    /// </summary>
    [Fact]
    public async Task An_excluded_file_loses_its_stale_entry_instead_of_keeping_the_old_path()
    {
        Seed(fileCount: 3);
        using (var db = _harness.CreateContext())
        {
            db.Files.Single(f => f.Id == 2).IsIncluded = false;
            db.SaveChanges();
        }

        await RenameFolderAsync("Foto", "Immagini");

        var rows = FtsRows();
        rows.Should().HaveCount(2);
        rows.Should().NotContain(r => r.Rowid == 2);
    }

    // ── cost, counted ─────────────────────────────────────────────────────────

    /// <summary>
    /// The measurement. 600 files: 1 200 statements before (a DELETE and an INSERT each), 2 now —
    /// and 2 is not "600 rounded down", it is one chunk of DIRECTORIES. The count grows with the
    /// number of folders in the renamed subtree, never with the number of files in it.
    /// </summary>
    [Fact]
    public async Task Six_hundred_files_cost_two_statements_instead_of_twelve_hundred()
    {
        Seed(fileCount: 600);
        _sql.Reset();

        await RenameFolderAsync("Foto", "Immagini");

        _sql.CountContaining("FileSearchIndex").Should().Be(2,
            "a DELETE and an INSERT … SELECT for the one directory the rename touched");
    }

    /// <summary>
    /// The set is named by DIRECTORY, so the rows that are not in the index cost nothing.
    ///
    /// This is the case that decides between the two ways of writing the fix. Pruning stale
    /// entries means the excluded and absent rows have to be covered too — and those are exactly
    /// what a narrowed filter piles up (step 11a also marks anything under an excluded folder
    /// absent). Handing over file ids would therefore have meant marshalling every one of them: a
    /// folder of 900 excluded files and 100 indexed ones would cost the statements of a thousand.
    /// Named by directory, it costs the same as a folder holding only the hundred.
    /// </summary>
    [Fact]
    public async Task A_folder_full_of_excluded_files_costs_no_more_than_a_folder_without_them()
    {
        Seed(fileCount: 1_000);
        using (var db = _harness.CreateContext())
        {
            // 900 of the thousand leave the index: a filter the user narrowed after scanning.
            foreach (var file in db.Files.Where(f => f.Id > 100))
                file.IsIncluded = false;
            db.SaveChanges();
        }
        _sql.Reset();

        await RenameFolderAsync("Foto", "Immagini");

        _sql.CountContaining("FileSearchIndex").Should().Be(2,
            "one chunk of directories — the 900 rows that are not in the index are not named");

        var rows = FtsRows();
        rows.Should().HaveCount(100);
        rows.Should().OnlyContain(r => r.Path.StartsWith(@"Immagini\", StringComparison.Ordinal));
    }

    /// <summary>
    /// And no file row — nor even a file id — leaves the database. Measured as bytes allocated,
    /// which is where the old shape, a full FileEntry per file, was paid for.
    /// </summary>
    [Fact]
    public async Task Twenty_times_more_files_does_not_cost_twenty_times_more()
    {
        var small = await AllocationOfRenameAsync(fileCount: 25);
        var large = await AllocationOfRenameAsync(fileCount: 500);

        // Flat, not merely sub-linear: what crosses the boundary is one id per DIRECTORY, and both
        // folders hold one. A factor of 2 is slack for jitter, not for growth.
        large.Should().BeLessThan(small * 2,
            "renaming a folder of 25 files cost {0} bytes and one of 500 cost {1}", small, large);
    }

    private static async Task<long> AllocationOfRenameAsync(int fileCount)
    {
        using var fixture = new FolderRenameFtsCostTests();
        fixture.Seed(fileCount);

        // Warm-up rename: the first pass through EF compiles its queries, which is a one-off
        // measured in megabytes and would drown the per-file cost this test is about.
        await fixture.RenameFolderAsync("Foto", "Immagini", sequenceOrder: 1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        await fixture.RenameFolderAsync("Immagini", "Archivio", sequenceOrder: 2);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ── harness ───────────────────────────────────────────────────────────────

    /// <summary>Runs the completed-RenameFolder index update for real.</summary>
    private async Task RenameFolderAsync(string from, string to, int sequenceOrder = 1)
    {
        await using var db = _harness.CreateContext(_sql);

        var job = new OperationJob
        {
            Type = JobType.RenameFolder,
            State = JobState.Completed,
            SourceVolumeId = VolId,
            TargetVolumeId = VolId,
            IsIntraVolume = true,
            SequenceOrder = sequenceOrder,
        };
        job.Items.Add(new OperationJobItem
        {
            SourceRelativePath = from,
            TargetRelativePath = to,
            SizeBytes = 0,
            State = JobItemState.Done,
        });
        db.OperationJobs.Add(job);
        await db.SaveChangesAsync();

        await TestProjection.Index(db, new FileSearchIndex(db))
            .UpdateAfterCompletionAsync(job, CancellationToken.None);
    }

    private void Seed(int fileCount)
    {
        using var db = _harness.CreateContext();

        db.Volumes.Add(new Volume
        {
            Id = VolId, VolumeGuid = @"\\?\Volume{aaa-1}\", FileSystem = "NTFS", IsOnline = true,
        });
        db.Directories.Add(new DirectoryNode
        {
            Id = FolderDirId, VolumeId = VolId, Name = "Foto",
            MaterializedPath = "Foto", IsMaterialized = true, IsPresent = true,
        });
        db.SaveChanges();

        for (int i = 0; i < fileCount; i++)
        {
            db.Files.Add(new FileEntry
            {
                Id = i + 1,
                VolumeId = VolId,
                DirectoryId = FolderDirId,
                Name = $"f{i:D5}.jpg",
                Extension = "jpg",
                Category = FileCategory.Image,
                SizeBytes = 10,
                FileCreatedUtc = DateTime.UtcNow,
                FileModifiedUtc = DateTime.UtcNow,
                IsIncluded = true,
                IsPresent = true,
                LastIndexedUtc = DateTime.UtcNow,
            });
        }
        db.SaveChanges();

        // Populate the index the way a scan would, so the rename is re-syncing real entries.
        new FileSearchIndex(db).SyncVolumeFromDbAsync(VolId, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Every row of the real FTS table — one reader, shared with the other suites.</summary>
    private List<(int Rowid, string Name, string Path)> FtsRows() => SqliteFts.Rows(_harness);
}
