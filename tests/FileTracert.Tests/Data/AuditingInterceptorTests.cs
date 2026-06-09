using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FileTracert.Data.Interceptors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

public sealed class AuditingInterceptorTests
{
    private static Volume NewVolume() => new()
    {
        VolumeGuid = Guid.NewGuid().ToString(),
        FileSystem = "NTFS",
        ScanEngine = VolumeScanEngine.UsnJournal,
    };

    [Fact]
    public async Task Insert_sets_both_created_and_updated()
    {
        using var harness = new SqliteInMemoryContext();
        var before = DateTime.UtcNow.AddSeconds(-1);

        await using var context = harness.CreateContext();
        var volume = NewVolume();
        context.Volumes.Add(volume);
        await context.SaveChangesAsync();

        var after = DateTime.UtcNow.AddSeconds(1);
        volume.CreatedUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        volume.UpdatedUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        volume.CreatedUtc.Should().Be(volume.UpdatedUtc);
    }

    [Fact]
    public async Task Update_changes_only_updated_and_keeps_created()
    {
        using var harness = new SqliteInMemoryContext();

        int id;
        DateTime originalCreated;
        await using (var context = harness.CreateContext())
        {
            var volume = NewVolume();
            context.Volumes.Add(volume);
            await context.SaveChangesAsync();
            id = volume.Id;
            originalCreated = volume.CreatedUtc;
        }

        // Small delay so the update timestamp is observably different.
        await Task.Delay(20);

        await using (var context = harness.CreateContext())
        {
            var volume = await context.Volumes.SingleAsync(x => x.Id == id);
            volume.Label = "changed";
            await context.SaveChangesAsync();

            volume.CreatedUtc.Should().Be(originalCreated);
            volume.UpdatedUtc.Should().BeAfter(originalCreated);
        }
    }

    [Fact]
    public async Task Non_auditable_entity_is_not_touched_by_interceptor()
    {
        // ExtensionCategory does not implement IAuditable: it has no audit columns
        // and the interceptor must skip it without error. Using a fresh schema with
        // auditing enabled.
        using var harness = new SqliteInMemoryContext();

        await using var context = harness.CreateContext();
        context.ExtensionCategories.Add(new ExtensionCategory
        {
            Extension = "xyz",
            Category = FileCategory.Other,
        });

        var act = async () => await context.SaveChangesAsync();
        await act.Should().NotThrowAsync();

        (await context.ExtensionCategories.AnyAsync(x => x.Extension == "xyz"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task FileEntry_audit_fields_are_independent_of_file_timestamps()
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
        var fileCreated = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fileModified = new DateTime(2002, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileEntry
        {
            Volume = volume,
            Directory = dir,
            Name = "photo.jpg",
            Extension = "jpg",
            Category = FileCategory.Image,
            SizeBytes = 1024,
            FileCreatedUtc = fileCreated,
            FileModifiedUtc = fileModified,
            Attributes = System.IO.FileAttributes.Normal,
            IsIncluded = true,
            IsPresent = true,
            LastIndexedUtc = DateTime.UtcNow,
        };
        context.Files.Add(file);
        await context.SaveChangesAsync();

        // File's own dates are preserved verbatim; row-audit dates are set by the interceptor (≈ now).
        file.FileCreatedUtc.Should().Be(fileCreated);
        file.FileModifiedUtc.Should().Be(fileModified);
        file.CreatedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        file.UpdatedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Without_interceptor_audit_fields_stay_default()
    {
        using var harness = new SqliteInMemoryContext(withAuditing: false);

        await using var context = harness.CreateContext();
        var volume = NewVolume();
        context.Volumes.Add(volume);
        await context.SaveChangesAsync();

        volume.CreatedUtc.Should().Be(default);
        volume.UpdatedUtc.Should().Be(default);
    }
}
