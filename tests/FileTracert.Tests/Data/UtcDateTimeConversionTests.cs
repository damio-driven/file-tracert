using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

/// <summary>
/// SQLite has no native datetime type: it stores TEXT and hands it back with
/// <see cref="DateTimeKind.Unspecified"/>. Without a global converter every
/// DB-sourced timestamp serialises without the trailing 'Z' and every UI clock
/// reads it as local time (review finding #12).
/// </summary>
public sealed class UtcDateTimeConversionTests
{
    [Fact]
    public async Task Non_nullable_DateTime_round_trips_as_Utc()
    {
        using var harness = new SqliteInMemoryContext();
        var seen = new DateTime(2026, 7, 3, 14, 20, 29, 912, DateTimeKind.Utc);

        int id;
        await using (var write = harness.CreateContext())
        {
            var volume = new Volume
            {
                VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
                FileSystem = "NTFS",
                ScanEngine = VolumeScanEngine.UsnJournal,
                LastSeenUtc = seen,
            };
            write.Volumes.Add(volume);
            await write.SaveChangesAsync();
            id = volume.Id;
        }

        await using var read = harness.CreateContext();
        var reloaded = await read.Volumes.SingleAsync(v => v.Id == id);

        reloaded.LastSeenUtc.Kind.Should().Be(DateTimeKind.Utc);
        reloaded.LastSeenUtc.Should().Be(seen);
    }

    [Fact]
    public async Task Nullable_DateTime_round_trips_as_Utc()
    {
        using var harness = new SqliteInMemoryContext();
        var scanned = new DateTime(2026, 7, 3, 23, 59, 59, 999, DateTimeKind.Utc);

        int id;
        await using (var write = harness.CreateContext())
        {
            var volume = new Volume
            {
                VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
                FileSystem = "NTFS",
                ScanEngine = VolumeScanEngine.UsnJournal,
                LastFullScanUtc = scanned,
            };
            write.Volumes.Add(volume);
            await write.SaveChangesAsync();
            id = volume.Id;
        }

        await using var read = harness.CreateContext();
        var reloaded = await read.Volumes.SingleAsync(v => v.Id == id);

        reloaded.LastFullScanUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        reloaded.LastFullScanUtc.Value.Should().Be(scanned);
    }

    /// <summary>
    /// A value that arrives already <see cref="DateTimeKind.Unspecified"/> (e.g. read
    /// from a file system API) must be persisted verbatim: shifting it by the local
    /// offset on write would move data that the whole codebase treats as UTC (§6).
    /// </summary>
    [Fact]
    public async Task Unspecified_kind_is_persisted_verbatim_not_shifted()
    {
        using var harness = new SqliteInMemoryContext();
        var modified = new DateTime(2026, 7, 3, 14, 20, 29, DateTimeKind.Unspecified);

        int id;
        await using (var write = harness.CreateContext())
        {
            var volume = new Volume
            {
                VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
                FileSystem = "NTFS",
                ScanEngine = VolumeScanEngine.UsnJournal,
            };
            write.Volumes.Add(volume);
            await write.SaveChangesAsync();

            var dir = new DirectoryNode
            {
                VolumeId = volume.Id,
                Name = "root",
                MaterializedPath = string.Empty,
                IsMaterialized = true,
            };
            write.Directories.Add(dir);
            await write.SaveChangesAsync();

            var file = new FileEntry
            {
                VolumeId = volume.Id,
                DirectoryId = dir.Id,
                Name = "a.jpg",
                Extension = "jpg",
                Category = FileCategory.Image,
                SizeBytes = 1,
                FileModifiedUtc = modified,
                IsIncluded = true,
                IsPresent = true,
            };
            write.Files.Add(file);
            await write.SaveChangesAsync();
            id = file.Id;
        }

        await using var read = harness.CreateContext();
        var reloaded = await read.Files.SingleAsync(f => f.Id == id);

        reloaded.FileModifiedUtc.Kind.Should().Be(DateTimeKind.Utc);
        reloaded.FileModifiedUtc.Should().Be(DateTime.SpecifyKind(modified, DateTimeKind.Utc));
    }
}
