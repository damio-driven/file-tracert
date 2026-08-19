using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

/// <summary>
/// E5 — what the Catalog's per-folder counters cost.
///
/// The measurement that shaped this fix is written down here because it contradicts half the
/// finding: SQLite is asked for `(DirectoryId = d AND PendingDirectoryId IS NULL) OR
/// PendingDirectoryId = d`, and with the statistics this application actually has — it never runs
/// <c>ANALYZE</c> — the planner already answers it with MULTI-INDEX OR, i.e. two index seeks, not
/// the table scans the finding assumed. Rewriting the two correlated sub-queries into grouped
/// queries was measured at 122–449 ms against 176–239 ms for the shape in place, on 300 000 files
/// across 499 sub-directories: inside the noise, for three round trips instead of one. So the
/// shape stays.
///
/// What was left over is real and is not a matter of milliseconds: the seek found the rows, then
/// SQLite had to fetch the TABLE ROW of every counted file to read two booleans. Counting one
/// listing of 499 folders × 601 files = ~300 000 row lookups, for two numbers on a badge. Putting
/// the flags in the index removes every one of them, which is what these tests assert — on the
/// plan, which is exact, rather than on a stopwatch, which is not.
///
/// It costs no extra index: both indexes start with the foreign key EF was already indexing on its
/// own, so they REPLACE the narrow ones instead of joining them.
/// </summary>
public sealed class CatalogCountIndexTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness = new();

    public void Dispose() => _harness.Dispose();

    private List<string> IndexNames()
    {
        using var db = _harness.CreateContext();
        return db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Files'")
            .ToList();
    }

    [Fact]
    public void The_covering_indexes_replace_the_narrow_FK_indexes_rather_than_joining_them()
    {
        var names = IndexNames();

        names.Should().Contain("IX_Files_DirectoryId_PendingDirectoryId_IsIncluded_IsPresent");
        names.Should().Contain("IX_Files_PendingDirectoryId_IsIncluded_IsPresent");

        // The whole point of leading with the FK: the write path pays for the same number of
        // B-trees per inserted row as before, and a scan inserts millions of rows.
        names.Should().NotContain("IX_Files_DirectoryId");
        names.Should().NotContain("IX_Files_PendingDirectoryId");
    }

    [Fact]
    public async Task The_file_counter_never_leaves_the_index()
    {
        using var db = _harness.CreateContext();
        var (volumeId, parentId) = await SeedAsync(db);

        // The claim is about a catalog with real weight — a four-row table is a thin place to
        // assert a query plan, even though SQLite plans from the schema (this application never
        // runs ANALYZE, as the class comment says). Bulked out with raw SQL: what is being
        // measured is the plan, and EF change tracking over thousands of entities is not.
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff");
#pragma warning disable EF1002 // every interpolated value is an int or produced right here
        await db.Database.ExecuteSqlRawAsync($"""
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 20000)
            INSERT INTO Files
              (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
               CreatedUtc, ModifiedUtc, Attributes, IsIncluded, IsPresent,
               LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
            SELECT {volumeId}, {parentId}, 'bulk' || n || '.jpg', 'jpg', 'Image', 1024,
                   '{now}', '{now}', 0, 1, 1, '{now}', 'None', '{now}', '{now}'
            FROM seq
            """);
#pragma warning restore EF1002

        // Verbatim the Catalog's counter predicate (CatalogController.GetChildren).
        var counter = db.Files.AsNoTracking()
            .Where(f => ((f.DirectoryId == parentId && f.PendingDirectoryId == null) || f.PendingDirectoryId == parentId) &&
                        f.IsIncluded && f.IsPresent)
            .Select(f => f.Id);

        // Asked as the Catalog asks it: a COUNT over that predicate, so the planner sees a
        // statement that needs no column beyond the ones the predicate names.
        var plan = await PlanOfAsync(db, counter.ToQueryString(), parentId, asCount: true);

        // The branch that carries the traffic — files sitting where they sit, no overlay — is
        // answered ENTIRELY from the index: that is the ~300 000 table lookups per listing that
        // stop happening. The other branch (files queued INTO this folder from elsewhere) still
        // resolves rows, because a MULTI-INDEX OR has to produce rowids to union its two halves;
        // it is also the branch that matches almost nothing, since an overlay is rare by nature.
        plan.Should().Contain("COVERING INDEX IX_Files_DirectoryId_PendingDirectoryId_IsIncluded_IsPresent");
        plan.Should().Contain("IX_Files_PendingDirectoryId_IsIncluded_IsPresent");
        plan.Should().NotContain("SCAN f");
    }

    [Fact]
    public async Task The_counter_still_answers_what_it_answered_before()
    {
        using var db = _harness.CreateContext();
        var (_, dirId) = await SeedAsync(db);
        var elsewhere = db.Directories.Single(d => d.MaterializedPath == "Altrove");

        // 4 files physically in the folder, one of them excluded and one absent → 2 count.
        // Plus one file that lives elsewhere but is queued INTO the folder → 3.
        // Plus one file physically in the folder but queued OUT of it → still 3.
        int Counted() => db.Files.Count(f =>
            ((f.DirectoryId == dirId && f.PendingDirectoryId == null) || f.PendingDirectoryId == dirId) &&
            f.IsIncluded && f.IsPresent);

        Counted().Should().Be(2);

        db.Files.Add(NewFile(db, dirId, "incoming.jpg"));
        await db.SaveChangesAsync();
        var incoming = db.Files.Single(f => f.Name == "incoming.jpg");
        incoming.DirectoryId = elsewhere.Id;
        incoming.PendingDirectoryId = dirId;
        incoming.PendingState = EntityPendingState.PendingMove;
        await db.SaveChangesAsync();

        Counted().Should().Be(3);

        var leaving = db.Files.First(f => f.DirectoryId == dirId && f.IsIncluded && f.IsPresent);
        leaving.PendingDirectoryId = elsewhere.Id;
        leaving.PendingState = EntityPendingState.PendingMove;
        await db.SaveChangesAsync();

        Counted().Should().Be(2);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task<(int VolumeId, int DirId)> SeedAsync(FileTracertDbContext db)
    {
        var volume = new Volume
        {
            VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
            IsOnline = true,
        };
        db.Volumes.Add(volume);
        await db.SaveChangesAsync();

        var dir = new DirectoryNode
        {
            VolumeId = volume.Id, Name = "Foto", MaterializedPath = "Foto", IsMaterialized = true,
        };
        var other = new DirectoryNode
        {
            VolumeId = volume.Id, Name = "Altrove", MaterializedPath = "Altrove", IsMaterialized = true,
        };
        db.Directories.AddRange(dir, other);
        await db.SaveChangesAsync();

        db.Files.AddRange(
            NewFile(db, dir.Id, "a.jpg"),
            NewFile(db, dir.Id, "b.jpg"),
            Tweak(NewFile(db, dir.Id, "excluded.tmp"), f => f.IsIncluded = false),
            Tweak(NewFile(db, dir.Id, "gone.jpg"), f => f.IsPresent = false));
        await db.SaveChangesAsync();

        return (volume.Id, dir.Id);
    }

    private static FileEntry Tweak(FileEntry file, Action<FileEntry> change)
    {
        change(file);
        return file;
    }

    private static FileEntry NewFile(FileTracertDbContext db, int dirId, string name) => new()
    {
        VolumeId = db.Directories.Find(dirId)!.VolumeId,
        DirectoryId = dirId,
        Name = name,
        Extension = name[(name.LastIndexOf('.') + 1)..],
        Category = FileCategory.Image,
        SizeBytes = 1024,
        FileCreatedUtc = DateTime.UtcNow,
        FileModifiedUtc = DateTime.UtcNow,
        IsIncluded = true,
        IsPresent = true,
        LastIndexedUtc = DateTime.UtcNow,
    };

    /// <summary>The plan SQLite chooses for the query EF really sends, as one string.</summary>
    private static async Task<string> PlanOfAsync(
        FileTracertDbContext db, string queryString, int parameter, bool asCount = false)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            // ToQueryString() prefixes a `.param set` preamble for readability — cut it, and
            // substitute the value so the planner sees a complete statement.
            var body = queryString[queryString.IndexOf("SELECT", StringComparison.Ordinal)..];
            body = body.Replace("@parentId", parameter.ToString());
            if (asCount) body = $"SELECT COUNT(*) FROM ({body})";
            cmd.CommandText = "EXPLAIN QUERY PLAN " + body;
            await using var reader = await cmd.ExecuteReaderAsync();

            var lines = new List<string>();
            while (await reader.ReadAsync()) lines.Add(reader.GetString(3));
            return string.Join("\n", lines);
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }
}
