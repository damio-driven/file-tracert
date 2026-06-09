using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

public sealed class DbContextIntegrationTests
{
    private static Volume NewVolume(string? guid = null) => new()
    {
        VolumeGuid = guid ?? Guid.NewGuid().ToString(),
        FileSystem = "NTFS",
        ScanEngine = VolumeScanEngine.UsnJournal,
    };

    [Fact]
    public async Task All_expected_tables_exist()
    {
        using var harness = new SqliteInMemoryContext();
        await using var context = harness.CreateContext();

        var tables = new List<string>();
        var conn = context.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        tables.Should().Contain(new[]
        {
            "Volumes", "WatchedRoots", "Directories", "Files",
            "OperationJobs", "OperationJobItems", "SpaceLedgerEntries",
            "ExtensionCategories", "AppSettings",
        });
    }

    [Fact]
    public async Task ExtensionCategories_seed_is_present_and_correct()
    {
        using var harness = new SqliteInMemoryContext();
        await using var context = harness.CreateContext();

        // 19 image + 13 video + 10 audio + 15 document + 8 archive = 65.
        (await context.ExtensionCategories.CountAsync()).Should().Be(65);

        (await context.ExtensionCategories.SingleAsync(x => x.Extension == "jpg")).Category
            .Should().Be(FileCategory.Image);
        (await context.ExtensionCategories.SingleAsync(x => x.Extension == "mp4")).Category
            .Should().Be(FileCategory.Video);
        (await context.ExtensionCategories.SingleAsync(x => x.Extension == "mp3")).Category
            .Should().Be(FileCategory.Audio);
        (await context.ExtensionCategories.SingleAsync(x => x.Extension == "pdf")).Category
            .Should().Be(FileCategory.Document);
        (await context.ExtensionCategories.SingleAsync(x => x.Extension == "zip")).Category
            .Should().Be(FileCategory.Archive);
    }

    [Fact]
    public async Task Enums_are_stored_as_strings()
    {
        using var harness = new SqliteInMemoryContext();
        await using var context = harness.CreateContext();

        var conn = context.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Category FROM ExtensionCategories WHERE Extension = 'jpg';";
        var raw = (string?)await cmd.ExecuteScalarAsync();
        raw.Should().Be("Image");
    }

    [Fact]
    public async Task Volume_directory_file_roundtrip_with_navigations_and_audit()
    {
        using var harness = new SqliteInMemoryContext();

        int fileId;
        await using (var context = harness.CreateContext())
        {
            var volume = NewVolume();
            var dir = new DirectoryNode
            {
                Volume = volume,
                Name = "photos",
                MaterializedPath = "photos",
                IsMaterialized = true,
            };
            var file = new FileEntry
            {
                Volume = volume,
                Directory = dir,
                Name = "a.jpg",
                Extension = "jpg",
                Category = FileCategory.Image,
                SizeBytes = 2048,
                FileCreatedUtc = DateTime.UtcNow,
                FileModifiedUtc = DateTime.UtcNow,
                Attributes = System.IO.FileAttributes.Normal,
                IsIncluded = true,
                IsPresent = true,
                LastIndexedUtc = DateTime.UtcNow,
            };
            context.Files.Add(file);
            await context.SaveChangesAsync();
            fileId = file.Id;
        }

        await using (var context = harness.CreateContext())
        {
            var file = await context.Files
                .Include(f => f.Directory)
                .Include(f => f.Volume)
                .SingleAsync(f => f.Id == fileId);

            file.Directory.Should().NotBeNull();
            file.Directory.Name.Should().Be("photos");
            file.Volume.Should().NotBeNull();
            file.Volume.FileSystem.Should().Be("NTFS");

            file.CreatedUtc.Should().NotBe(default);
            file.UpdatedUtc.Should().NotBe(default);
            file.Volume.CreatedUtc.Should().NotBe(default);
            file.Directory.CreatedUtc.Should().NotBe(default);
        }
    }

    [Fact]
    public async Task Unique_usn_ref_per_volume_is_enforced()
    {
        using var harness = new SqliteInMemoryContext();

        await using var context = harness.CreateContext();
        var volume = NewVolume();
        var dir = new DirectoryNode
        {
            Volume = volume,
            Name = "root",
            MaterializedPath = "",
            IsMaterialized = true,
        };
        FileEntry MakeFile(string name) => new()
        {
            Volume = volume,
            Directory = dir,
            Name = name,
            Extension = "jpg",
            Category = FileCategory.Image,
            SizeBytes = 1,
            FileCreatedUtc = DateTime.UtcNow,
            FileModifiedUtc = DateTime.UtcNow,
            Attributes = System.IO.FileAttributes.Normal,
            UsnFileRef = 42,
            IsIncluded = true,
            IsPresent = true,
            LastIndexedUtc = DateTime.UtcNow,
        };

        context.Files.Add(MakeFile("first.jpg"));
        await context.SaveChangesAsync();

        context.Files.Add(MakeFile("second.jpg"));
        var act = async () => await context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Null_usn_refs_do_not_collide_due_to_filtered_index()
    {
        using var harness = new SqliteInMemoryContext();

        await using var context = harness.CreateContext();
        var volume = NewVolume();
        var dir = new DirectoryNode
        {
            Volume = volume,
            Name = "root",
            MaterializedPath = "",
            IsMaterialized = true,
        };
        FileEntry MakeFile(string name) => new()
        {
            Volume = volume,
            Directory = dir,
            Name = name,
            Extension = "jpg",
            Category = FileCategory.Image,
            SizeBytes = 1,
            FileCreatedUtc = DateTime.UtcNow,
            FileModifiedUtc = DateTime.UtcNow,
            Attributes = System.IO.FileAttributes.Normal,
            UsnFileRef = null,
            IsIncluded = true,
            IsPresent = true,
            LastIndexedUtc = DateTime.UtcNow,
        };

        context.Files.Add(MakeFile("a.jpg"));
        context.Files.Add(MakeFile("b.jpg"));

        var act = async () => await context.SaveChangesAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OperationJob_with_distinct_source_and_target_volumes_roundtrips()
    {
        using var harness = new SqliteInMemoryContext();

        int jobId;
        await using (var context = harness.CreateContext())
        {
            var source = NewVolume();
            var target = NewVolume();
            context.Volumes.AddRange(source, target);
            await context.SaveChangesAsync();

            var job = new OperationJob
            {
                Type = JobType.MoveFile,
                State = JobState.Pending,
                BlockReason = JobBlockReason.None,
                SourceVolumeId = source.Id,
                TargetVolumeId = target.Id,
                TargetRelativePath = "dest",
                IsIntraVolume = false,
                EstimateIsLive = true,
                SequenceOrder = 1,
            };
            context.OperationJobs.Add(job);
            await context.SaveChangesAsync();
            jobId = job.Id;
        }

        await using (var context = harness.CreateContext())
        {
            var job = await context.OperationJobs
                .Include(j => j.SourceVolume)
                .Include(j => j.TargetVolume)
                .SingleAsync(j => j.Id == jobId);

            job.SourceVolume.Should().NotBeNull();
            job.TargetVolume.Should().NotBeNull();
            job.SourceVolumeId.Should().NotBe(job.TargetVolumeId);
            job.State.Should().Be(JobState.Pending);
        }
    }

    [Fact]
    public async Task OperationJob_cascade_deletes_items_and_ledger_entries()
    {
        using var harness = new SqliteInMemoryContext();

        int jobId;
        await using (var context = harness.CreateContext())
        {
            var volume = NewVolume();
            context.Volumes.Add(volume);
            await context.SaveChangesAsync();

            var job = new OperationJob
            {
                Type = JobType.MoveFile,
                State = JobState.Pending,
                BlockReason = JobBlockReason.None,
                TargetVolumeId = volume.Id,
                SequenceOrder = 1,
            };
            job.Items.Add(new OperationJobItem
            {
                SourceRelativePath = "a",
                TargetRelativePath = "b",
                SizeBytes = 10,
                State = JobItemState.Pending,
            });
            job.LedgerEntries.Add(new SpaceLedgerEntry
            {
                Volume = volume,
                DeltaBytes = 10,
                IsActive = true,
            });
            context.OperationJobs.Add(job);
            await context.SaveChangesAsync();
            jobId = job.Id;
        }

        await using (var context = harness.CreateContext())
        {
            var job = await context.OperationJobs.SingleAsync(j => j.Id == jobId);
            context.OperationJobs.Remove(job);
            await context.SaveChangesAsync();

            (await context.OperationJobItems.AnyAsync()).Should().BeFalse();
            (await context.SpaceLedgerEntries.AnyAsync()).Should().BeFalse();
        }
    }

    [Fact]
    public async Task AppSettings_json_collections_roundtrip()
    {
        using var harness = new SqliteInMemoryContext();

        await using (var context = harness.CreateContext())
        {
            context.AppSettings.Add(new AppSettings
            {
                Id = 1,
                ApiToken = "tok",
                SpaceMarginPercent = 3,
                DefaultExtensionFilter = new List<string> { "jpg", "png" },
                ExcludedPaths = new List<string> { "Windows", "$Recycle.Bin" },
            });
            await context.SaveChangesAsync();
        }

        await using (var context = harness.CreateContext())
        {
            var settings = await context.AppSettings.SingleAsync();
            settings.Id.Should().Be(1);
            settings.DefaultExtensionFilter.Should().BeEquivalentTo("jpg", "png");
            settings.ExcludedPaths.Should().BeEquivalentTo("Windows", "$Recycle.Bin");
        }
    }
}
