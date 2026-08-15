using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
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

        var fts = new RecordingFts();
        await using (var db = _harness.CreateContext())
        {
            var job = await db.OperationJobs.Include(j => j.Items).SingleAsync();
            var updater = new IndexUpdater(db, fts, NullLogger<IndexUpdater>.Instance);
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

        // Every file's FTS entry was re-pointed to its projected path.
        fts.Upserts.Should().HaveCount(3);
        fts.Upserts.Should().ContainSingle(u => u.Id == 3 && u.Path == @"Archive\Media\B\f3.bin");
    }

    private static OperationJobItem Item(int fileId, string src, string dst) => new()
    {
        FileId = fileId, SourceRelativePath = src, TargetRelativePath = dst,
        SizeBytes = 10, State = JobItemState.Done,
        CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
    };
}
