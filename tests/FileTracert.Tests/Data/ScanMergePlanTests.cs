using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace FileTracert.Tests.Data;

/// <summary>
/// The plan that must NOT change.
///
/// <para>The scan merge resolves a staged row by <c>VolumeId = ? AND DirectoryId = ? AND Name = ?
/// ORDER BY Id LIMIT 1</c>, once per file in every batch — the hottest statement of a re-scan. It
/// relies on <c>IX_Files_VolumeId_DirectoryId</c> stopping at DirectoryId: the entries of one
/// directory are then already in Id order (Id is the rowid), so the ORDER BY costs nothing and the
/// LIMIT stops at the first matching name.</para>
///
/// <para>Step 14c added an index to this table, which is exactly the kind of change that can move a
/// planner off that path — and the failure would be silent: re-scans get slower, the suite stays
/// green. So it is asserted, on the SQL the writer really emits
/// (<see cref="CapturingSqliteConnection"/>), not on a copy.</para>
/// </summary>
public sealed class ScanMergePlanTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _out;
    private CapturingSqliteConnection _connection = null!;
    private SqliteInMemoryContext _harness = null!;
    private int _volumeId;
    private int _rootId;

    public ScanMergePlanTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _connection = new CapturingSqliteConnection("Data Source=:memory:");
        _harness = new SqliteInMemoryContext(connection: _connection);

        await using var ctx = _harness.CreateContext();
        var volume = new Volume
        {
            VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
            IsOnline = true,
        };
        ctx.Volumes.Add(volume);
        await ctx.SaveChangesAsync();
        _volumeId = volume.Id;

        var root = new DirectoryNode
        {
            VolumeId = volume.Id,
            Name = string.Empty,
            MaterializedPath = string.Empty,
            IsMaterialized = true,
        };
        ctx.Directories.Add(root);
        await ctx.SaveChangesAsync();
        _rootId = root.Id;
    }

    public Task DisposeAsync()
    {
        _harness.Dispose();
        return Task.CompletedTask;
    }

    private FileEntry Scanned(string name) => new()
    {
        VolumeId = _volumeId,
        DirectoryId = _rootId,
        Name = name,
        Extension = "jpg",
        Category = FileCategory.Image,
        SizeBytes = 10,
        FileCreatedUtc = T0,
        FileModifiedUtc = T0,
        IsIncluded = true,
        IsPresent = true,
        LastIndexedUtc = T0,
    };

    [Fact]
    public async Task The_merge_still_resolves_a_staged_row_through_the_volume_directory_index()
    {
        await using (var ctx = _harness.CreateContext())
        {
            await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                _volumeId, [Scanned("a.jpg"), Scanned("b.jpg")], T0, CancellationToken.None);
        }

        _connection.Reset();

        // A second merge over the same files: this is the run that takes the matching path.
        await using (var ctx = _harness.CreateContext())
        {
            var result = await new BulkIndexWriter(ctx).MergeScannedFilesAsync(
                _volumeId, [Scanned("a.jpg"), Scanned("b.jpg")], T0.AddMinutes(1), CancellationToken.None);

            result.Updated.Should().Be(2, "otherwise the statement under test never ran");
        }

        var statement = _connection.Statements
            .Last(s => s.Sql.Contains("MatchedId = (SELECT", StringComparison.Ordinal));

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + statement.Sql;
        foreach (var (name, value) in statement.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var plan = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        foreach (var line in plan)
        {
            _out.WriteLine(line);
        }

        plan.Should().Contain(l => l.Contains("IX_Files_VolumeId_DirectoryId", StringComparison.Ordinal),
            "this lookup runs once per file of every batch; on any other index it becomes a walk " +
            "of the whole volume, and nothing else in the suite would notice");
        plan.Should().NotContain(l => l.Contains("TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal),
            "Id is the rowid, so inside one (VolumeId, DirectoryId) group the entries are already " +
            "in Id order — that is what makes the ORDER BY free and the LIMIT able to stop early");
    }
}
