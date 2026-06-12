using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

public sealed class BulkIndexWriterTests
{
    private static async Task<int> SeedVolumeAsync(SqliteInMemoryContext harness)
    {
        await using var ctx = harness.CreateContext();
        var volume = new Volume { VolumeGuid = Guid.NewGuid().ToString(), FileSystem = "NTFS", ScanEngine = VolumeScanEngine.UsnJournal };
        ctx.Volumes.Add(volume);
        await ctx.SaveChangesAsync();
        return volume.Id;
    }

    [Fact]
    public async Task BulkInsertDirectories_writes_back_identities()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = await SeedVolumeAsync(harness);

        await using var ctx = harness.CreateContext();
        var writer = new BulkIndexWriter(ctx);
        var root = new DirectoryNode { VolumeId = volumeId, Name = "", MaterializedPath = "", IsMaterialized = true };

        await writer.BulkInsertDirectoriesAsync([root], CancellationToken.None);

        root.Id.Should().BeGreaterThan(0);
        (await ctx.Directories.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task BulkInsertFiles_persists_and_stamps_row_audit()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = await SeedVolumeAsync(harness);

        await using var ctx = harness.CreateContext();
        var writer = new BulkIndexWriter(ctx);

        var dir = new DirectoryNode { VolumeId = volumeId, Name = "", MaterializedPath = "", IsMaterialized = true };
        await writer.BulkInsertDirectoriesAsync([dir], CancellationToken.None);

        var file = new FileEntry
        {
            VolumeId = volumeId,
            DirectoryId = dir.Id,
            Name = "x.jpg",
            Extension = "jpg",
            Category = FileCategory.Image,
            SizeBytes = 42,
            FileCreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FileModifiedUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            IsIncluded = true,
            IsPresent = true,
            LastIndexedUtc = DateTime.UtcNow,
        };

        await writer.BulkInsertFilesAsync([file], CancellationToken.None);

        await using var read = harness.CreateContext();
        var persisted = await read.Files.SingleAsync();
        persisted.Name.Should().Be("x.jpg");
        persisted.SizeBytes.Should().Be(42);
        persisted.Category.Should().Be(FileCategory.Image);
        persisted.CreatedUtc.Should().NotBe(default); // RowCreatedUtc stamped by the writer
    }

    [Fact]
    public async Task BulkUpsertFiles_splits_insert_and_update()
    {
        using var harness = new SqliteInMemoryContext();
        var volumeId = await SeedVolumeAsync(harness);

        await using var ctx = harness.CreateContext();
        var writer = new BulkIndexWriter(ctx);
        var dir = new DirectoryNode { VolumeId = volumeId, Name = "", MaterializedPath = "", IsMaterialized = true };
        await writer.BulkInsertDirectoriesAsync([dir], CancellationToken.None);

        var initial = new FileEntry
        {
            VolumeId = volumeId, DirectoryId = dir.Id, Name = "a.txt", Extension = "txt",
            Category = FileCategory.Document, SizeBytes = 1, IsIncluded = true, IsPresent = true,
            LastIndexedUtc = DateTime.UtcNow,
        };
        await writer.BulkInsertFilesAsync([initial], CancellationToken.None);

        // BulkInsert does not write back identities (hot path), so fetch the id.
        var existingId = await ctx.Files.Where(f => f.Name == "a.txt").Select(f => f.Id).SingleAsync();

        // One update (existing id) + one insert (id 0).
        var update = new FileEntry
        {
            Id = existingId, VolumeId = volumeId, DirectoryId = dir.Id, Name = "a.txt", Extension = "txt",
            Category = FileCategory.Document, SizeBytes = 999, IsIncluded = true, IsPresent = true,
            LastIndexedUtc = DateTime.UtcNow,
        };
        var added = new FileEntry
        {
            VolumeId = volumeId, DirectoryId = dir.Id, Name = "b.txt", Extension = "txt",
            Category = FileCategory.Document, SizeBytes = 2, IsIncluded = true, IsPresent = true,
            LastIndexedUtc = DateTime.UtcNow,
        };

        await writer.BulkUpsertFilesAsync([update, added], CancellationToken.None);

        await using var read = harness.CreateContext();
        (await read.Files.CountAsync()).Should().Be(2);
        (await read.Files.SingleAsync(f => f.Name == "a.txt")).SizeBytes.Should().Be(999);
    }
}
