using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// C5: a cross-volume folder move re-points every file's index row and its FTS entry. The updater
/// must do this as one batch (a single SaveChanges) rather than one round-trip per file, while
/// leaving every record correctly moved.
/// </summary>
public sealed class IndexUpdaterTests : IDisposable
{
    private const int SrcVol = 1;
    private const int TgtVol = 2;

    private readonly SqliteInMemoryContext _harness;

    public IndexUpdaterTests()
    {
        _harness = new SqliteInMemoryContext();
        using var setup = _harness.CreateContext();
        setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

        // EnsureCreated builds the EF tables but not the virtual one — the search index tests
        // create it the same way. It is here so the FTS assertions below can run against the REAL
        // FileSearchIndex rather than a fake of the component whose behaviour is in question.
        setup.Database.ExecuteSqlRaw("""
            CREATE VIRTUAL TABLE IF NOT EXISTS FileSearchIndex USING fts5(
                name,
                path,
                tokenize="unicode61 remove_diacritics 2 separators '\._-'"
            );
            """);
    }

    /// <summary>Every row of the real FTS table, as (rowid, name, path).</summary>
    private List<(int Rowid, string Name, string Path)> FtsRows()
    {
        using var db = _harness.CreateContext();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT rowid, name, path FROM FileSearchIndex ORDER BY rowid";
            using var reader = cmd.ExecuteReader();
            var rows = new List<(int, string, string)>();
            while (reader.Read())
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            return rows;
        }
        finally { db.Database.CloseConnection(); }
    }

    public void Dispose() => _harness.Dispose();

    /// <summary>FTS fake that records every upsert so the batch coverage can be asserted.</summary>
    private sealed class RecordingFts : IFileSearchIndex
    {
        public List<(int Id, string Name, string Path)> Upserts { get; } = [];
        public Task ClearVolumeAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
        public Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
        public Task RebuildAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SyncFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct) => Task.CompletedTask;
        public Task SyncDirectoriesAsync(IReadOnlyCollection<int> directoryIds, CancellationToken ct) => Task.CompletedTask;
        public Task PruneVolumeAsync(int volumeId, CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAsync(int fileId, string name, string path, CancellationToken ct)
        { Upserts.Add((fileId, name, path)); return Task.CompletedTask; }
        public Task RemoveAsync(int fileId, CancellationToken ct) => Task.CompletedTask;
        public Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct)
            => Task.FromResult(new PagedResult<int>([], 0, query.Skip, query.Take));
    }

    [Fact]
    public async Task MoveFolder_cross_volume_repoints_every_file_and_its_fts_entry()
    {
        using (var db = _harness.CreateContext())
        {
            db.Volumes.AddRange(
                new Volume { Id = SrcVol, VolumeGuid = @"\\?\Volume{s}\", FileSystem = "NTFS", IsOnline = true },
                new Volume { Id = TgtVol, VolumeGuid = @"\\?\Volume{t}\", FileSystem = "NTFS", IsOnline = true });

            db.Directories.AddRange(
                new DirectoryNode { Id = 50, VolumeId = SrcVol, Name = "Media", MaterializedPath = "Media", IsMaterialized = true },
                new DirectoryNode { Id = 51, VolumeId = SrcVol, ParentId = 50, Name = "A", MaterializedPath = @"Media\A", IsMaterialized = true },
                new DirectoryNode { Id = 52, VolumeId = SrcVol, ParentId = 50, Name = "B", MaterializedPath = @"Media\B", IsMaterialized = true });

            for (int id = 1; id <= 3; id++)
            {
                int dirId = id <= 2 ? 51 : 52;
                db.Files.Add(new FileEntry
                {
                    Id = id, VolumeId = SrcVol, DirectoryId = dirId, Name = $"f{id}.bin", Extension = "bin",
                    Category = FileCategory.Other, SizeBytes = 10, IsPresent = true, IsIncluded = true,
                    FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
                });
            }

            var job = new OperationJob
            {
                Type = JobType.MoveFolder, State = JobState.Completed, IsIntraVolume = false,
                SourceVolumeId = SrcVol, TargetVolumeId = TgtVol, TargetRelativePath = @"Archive\Media",
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            };
            job.Items.Add(Item(1, @"Media\A\f1.bin", @"Archive\Media\A\f1.bin"));
            job.Items.Add(Item(2, @"Media\A\f2.bin", @"Archive\Media\A\f2.bin"));
            job.Items.Add(Item(3, @"Media\B\f3.bin", @"Archive\Media\B\f3.bin"));
            db.OperationJobs.Add(job);
            db.SaveChanges();
        }

        await using (var db = _harness.CreateContext())
        {
            var job = await db.OperationJobs.Include(j => j.Items).SingleAsync();
            // The REAL search index over the real FTS5 table: E4 moved the name/path rule out of
            // this class and into the SQL that owns it, so a fake here would assert nothing about
            // the outcome any more.
            var updater = TestProjection.Index(db, new FileSearchIndex(db));
            await updater.UpdateAfterCompletionAsync(job, CancellationToken.None);
        }

        await using (var probe = _harness.CreateContext())
        {
            var files = await probe.Files.Include(f => f.Directory).OrderBy(f => f.Id).ToListAsync();

            // Every file moved to the target volume.
            files.Should().OnlyContain(f => f.VolumeId == TgtVol);

            // …and to a freshly-created target directory with the projected path.
            files.Single(f => f.Id == 1).Directory.MaterializedPath.Should().Be(@"Archive\Media\A");
            files.Single(f => f.Id == 2).Directory.MaterializedPath.Should().Be(@"Archive\Media\A");
            files.Single(f => f.Id == 3).Directory.MaterializedPath.Should().Be(@"Archive\Media\B");
        }

        // Every file's FTS entry was re-pointed to its new path — asserted on the index itself,
        // one row per moved file, name and path both, and nothing left over.
        FtsRows().Should().Equal(
            (1, "f1.bin", @"Archive\Media\A\f1.bin"),
            (2, "f2.bin", @"Archive\Media\A\f2.bin"),
            (3, "f3.bin", @"Archive\Media\B\f3.bin"));
    }

    /// <summary>
    /// Finding 6. The FRN is an identity INSIDE one volume — low MFT indices repeat on every
    /// NTFS volume — so carrying the source FRN over to the target can collide with a file
    /// already indexed there. The unique <c>(VolumeId, UsnFileRef)</c> index then throws AFTER
    /// the bytes have already been moved, which flips a successful job to Failed.
    /// </summary>
    [Fact]
    public async Task MoveFile_cross_volume_drops_the_source_FRN()
    {
        const long SharedFrn = 42;

        using (var db = _harness.CreateContext())
        {
            SeedVolumes(db);
            db.Directories.AddRange(
                Dir(50, SrcVol, "Docs", "Docs"),
                Dir(60, TgtVol, "Archive", "Archive"));

            // The moving file, and a file ALREADY on the target that happens to own the same FRN.
            db.Files.Add(File(1, SrcVol, 50, "report.txt", SharedFrn));
            db.Files.Add(File(9, TgtVol, 60, "unrelated.txt", SharedFrn));

            var job = Job(JobType.MoveFile, @"Archive\report.txt");
            job.Items.Add(Item(1, @"Docs\report.txt", @"Archive\report.txt"));
            db.OperationJobs.Add(job);
            db.SaveChanges();
        }

        await using (var db = _harness.CreateContext())
        {
            var job = await db.OperationJobs.Include(j => j.Items).SingleAsync();
            await TestProjection.Index(db, new RecordingFts()).UpdateAfterCompletionAsync(job, CancellationToken.None);
        }

        await using (var probe = _harness.CreateContext())
        {
            var moved = await probe.Files.SingleAsync(f => f.Id == 1);
            moved.VolumeId.Should().Be(TgtVol);
            moved.UsnFileRef.Should().BeNull("an FRN from another volume means nothing here");

            // The file that was already on the target keeps its own identity untouched.
            (await probe.Files.SingleAsync(f => f.Id == 9)).UsnFileRef.Should().Be(SharedFrn);
        }
    }

    /// <summary>Same rule on the folder path: every file the cross-volume move re-points.</summary>
    [Fact]
    public async Task MoveFolder_cross_volume_drops_the_source_FRN_of_every_file()
    {
        using (var db = _harness.CreateContext())
        {
            SeedVolumes(db);
            db.Directories.AddRange(
                Dir(50, SrcVol, "Media", "Media"),
                Dir(60, TgtVol, "Keep", "Keep"));

            db.Files.Add(File(1, SrcVol, 50, "a.bin", 7));
            db.Files.Add(File(2, SrcVol, 50, "b.bin", 8));
            // Already on the target with an FRN that collides with the second moving file.
            db.Files.Add(File(9, TgtVol, 60, "old.bin", 8));

            var job = Job(JobType.MoveFolder, @"Keep\Media");
            job.Items.Add(FolderMarker("Media", @"Keep\Media"));
            job.Items.Add(Item(1, @"Media\a.bin", @"Keep\Media\a.bin"));
            job.Items.Add(Item(2, @"Media\b.bin", @"Keep\Media\b.bin"));
            db.OperationJobs.Add(job);
            db.SaveChanges();
        }

        await using (var db = _harness.CreateContext())
        {
            var job = await db.OperationJobs.Include(j => j.Items).SingleAsync();
            await TestProjection.Index(db, new RecordingFts()).UpdateAfterCompletionAsync(job, CancellationToken.None);
        }

        await using (var probe = _harness.CreateContext())
        {
            var moved = await probe.Files.Where(f => f.Id == 1 || f.Id == 2).ToListAsync();
            moved.Should().OnlyContain(f => f.VolumeId == TgtVol && f.UsnFileRef == null);
            (await probe.Files.SingleAsync(f => f.Id == 9)).UsnFileRef.Should().Be(8);
        }
    }

    /// <summary>
    /// The cancel reconciliation re-points landed items to the target volume too, so it carries
    /// the same rule — otherwise a cancelled cross-volume job leaves the very violation the
    /// completion path now avoids.
    ///
    /// <para>It also runs the REAL search index (step 11e): this is the third of the three call
    /// sites that stopped upserting per file, and the one where the sync moved from BEFORE the
    /// <c>SaveChanges</c> to after it — the old order rebuilt each entry from a row whose new
    /// <c>DirectoryId</c> was not written yet, and got away with it only because the path was also
    /// passed in by hand. Reading the entry back is what shows the new order is right.</para>
    /// </summary>
    [Fact]
    public async Task Cancel_reconciliation_drops_the_source_FRN_of_landed_items()
    {
        using (var db = _harness.CreateContext())
        {
            SeedVolumes(db);
            db.Directories.AddRange(
                Dir(50, SrcVol, "Docs", "Docs"),
                Dir(60, TgtVol, "Archive", "Archive"));

            db.Files.Add(File(1, SrcVol, 50, "report.txt", 42));
            db.Files.Add(File(9, TgtVol, 60, "unrelated.txt", 42));

            var job = Job(JobType.MoveFile, @"Archive\report.txt");
            job.State = JobState.Cancelled;
            job.Items.Add(Item(1, @"Docs\report.txt", @"Archive\report.txt"));
            db.OperationJobs.Add(job);
            db.SaveChanges();

        }

        // Index both files where they physically are, the way a scan would: what follows is a
        // RE-sync of an existing entry, which is where a stale one would survive.
        await using (var db = _harness.CreateContext())
        {
            await new FileSearchIndex(db).RebuildAsync(CancellationToken.None);
        }

        await using (var db = _harness.CreateContext())
        {
            var job = await db.OperationJobs.Include(j => j.Items).SingleAsync();
            await TestProjection.Index(db, new FileSearchIndex(db))
                .ReconcileCancelledJobAsync(job, CancellationToken.None);
        }

        await using (var probe = _harness.CreateContext())
        {
            var moved = await probe.Files.SingleAsync(f => f.Id == 1);
            moved.VolumeId.Should().Be(TgtVol);
            moved.UsnFileRef.Should().BeNull();
        }

        // The landed item is searchable where it now lives — built from the saved row, not from a
        // path handed over alongside it. The untouched file keeps the entry it already had.
        FtsRows().Should().Contain((1, "report.txt", @"Archive\report.txt"));
        FtsRows().Should().Contain((9, "unrelated.txt", @"Archive\unrelated.txt"));
    }

    // ── seed helpers ──────────────────────────────────────────────────────────

    private static void SeedVolumes(FileTracert.Data.FileTracertDbContext db) =>
        db.Volumes.AddRange(
            new Volume { Id = SrcVol, VolumeGuid = @"\\?\Volume{s}\", FileSystem = "NTFS", IsOnline = true },
            new Volume { Id = TgtVol, VolumeGuid = @"\\?\Volume{t}\", FileSystem = "NTFS", IsOnline = true });

    private static DirectoryNode Dir(int id, int volumeId, string name, string path) => new()
    {
        Id = id, VolumeId = volumeId, Name = name, MaterializedPath = path, IsMaterialized = true,
    };

    private static FileEntry File(int id, int volumeId, int dirId, string name, long? frn) => new()
    {
        Id = id, VolumeId = volumeId, DirectoryId = dirId, Name = name,
        Extension = FileTracert.Business.Filtering.FileFilter.GetExtension(name),
        Category = FileCategory.Other, SizeBytes = 10, IsPresent = true, IsIncluded = true,
        UsnFileRef = frn,
        FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, LastIndexedUtc = DateTime.UtcNow,
    };

    private static OperationJob Job(JobType type, string targetPath) => new()
    {
        Type = type, State = JobState.Completed, IsIntraVolume = false,
        SourceVolumeId = SrcVol, TargetVolumeId = TgtVol, TargetRelativePath = targetPath,
        SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
    };

    private static OperationJobItem FolderMarker(string src, string dst) => new()
    {
        FileId = null, SourceRelativePath = src, TargetRelativePath = dst,
        SizeBytes = 0, State = JobItemState.Done,
        CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
    };

    private static OperationJobItem Item(int fileId, string src, string dst) => new()
    {
        FileId = fileId, SourceRelativePath = src, TargetRelativePath = dst,
        SizeBytes = 10, State = JobItemState.Done,
        CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
    };
}
