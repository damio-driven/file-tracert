using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

/// <summary>
/// A catalog big enough that one real search statement takes long enough to be cancelled
/// half-way through — seeded once for the whole class, because seeding it is the slow part
/// and every test that uses it only reads.
///
/// <para>The slowness is made of real data and a real query, never a sleep: <see cref="Rows"/>
/// files all carrying the same token, so the FTS prefix term matches every one of them, and a
/// filter that matches none of them so the statement has to walk the whole match set before it can
/// answer. That is the shape step 13 measured on 742 033 real files.</para>
/// </summary>
public sealed class BigCatalogFixture : IAsyncLifetime
{
    /// <summary>
    /// Sized so the guarded statement is comfortably longer than the delay a test waits before
    /// cancelling. <see cref="ReadCancellationTests"/> asserts the baseline it measures is big
    /// enough and says so if a faster machine ever makes it too small.
    /// </summary>
    public const int Rows = 400_000;

    public SqliteInMemoryContext Harness { get; private set; } = null!;
    public FileTracertDbContext Context { get; private set; } = null!;
    public int VolumeId { get; private set; }

    public async Task InitializeAsync()
    {
        Harness = new SqliteInMemoryContext();
        Context = Harness.CreateContext();
        SqliteFts.Create(Context);

        var volume = new Volume
        {
            VolumeGuid = $@"\?\Volume{{{Guid.NewGuid()}}}\",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
            IsOnline = true,
        };
        Context.Volumes.Add(volume);
        await Context.SaveChangesAsync();
        VolumeId = volume.Id;

        var root = new DirectoryNode
        {
            VolumeId = volume.Id,
            Name = string.Empty,
            MaterializedPath = string.Empty,
            IsMaterialized = true,
        };
        Context.Directories.Add(root);
        await Context.SaveChangesAsync();

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff");
        // Every interpolated value is an int or a value produced right here — nothing user-supplied.
#pragma warning disable EF1002
        await Context.Database.ExecuteSqlRawAsync($"""
            WITH RECURSIVE seq(n) AS (
                SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {Rows})
            INSERT INTO Files
              (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
               CreatedUtc, ModifiedUtc, Attributes, IsIncluded, ExcludedByType, ExcludedByRoot,
               ExcludedByScan, IsPresent, LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
            SELECT {volume.Id}, {root.Id}, 'match' || n || '.bin', 'bin', 'Other', 1024 * n,
                   '{now}', '{now}', 0, 1, 0, 0, 0, 1, '{now}', 'None', '{now}', '{now}'
            FROM seq
            """);
#pragma warning restore EF1002

        await new FileSearchIndex(Context).SyncVolumeFromDbAsync(VolumeId, CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Harness.Dispose();
        return Task.CompletedTask;
    }
}
