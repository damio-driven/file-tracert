using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Business;

/// <summary>
/// P2. <c>MaterializedPath</c> is compared two ways: in SQL, where <c>==</c> used the column's
/// default BINARY collation, and in memory, where every cache and predicate is
/// <c>OrdinalIgnoreCase</c>. Windows paths are case-insensitive, so the two answers disagreed on
/// a case-variant — and the find-or-create walk, finding nothing, inserted a SECOND
/// <c>DirectoryNode</c> next to the row that was already there.
///
/// Fixed at the column, not at the call site: one collation, so the SQL and the in-memory rule
/// say the same thing everywhere, including the callers nobody thought to audit.
/// </summary>
public sealed class DirectoryCollationTests : IDisposable
{
    private const int VolumeId = 1;
    private readonly SqliteInMemoryContext _harness = new();

    public DirectoryCollationTests()
    {
        using var setup = _harness.CreateContext();
        setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        setup.Volumes.Add(new Volume
        {
            Id = VolumeId, VolumeGuid = @"\\?\Volume{p}\", FileSystem = "NTFS", IsOnline = true,
        });
        setup.Directories.Add(new DirectoryNode
        {
            Id = 10, VolumeId = VolumeId, Name = "Photos",
            MaterializedPath = "Photos", IsMaterialized = true, IsPresent = true,
        });
        setup.SaveChanges();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Resolving_a_case_variant_path_reuses_the_existing_directory_row()
    {
        await using (var db = _harness.CreateContext())
        {
            var leaf = await TestProjection.Resolver(db)
                .FindOrCreateMaterializedAsync(VolumeId, @"photos\x", CancellationToken.None);

            leaf.ParentId.Should().Be(10, "the walk must recognise the row that is already there");
        }

        await using var probe = _harness.CreateContext();
        var paths = await probe.Directories.Select(d => d.MaterializedPath).ToListAsync();
        paths.Should().BeEquivalentTo(["Photos", @"photos\x"],
            "one row per folder — a case variant is the same folder on Windows");
    }

    /// <summary>
    /// The projection walk (§5) shares the same resolver, so an enqueue that invents a target
    /// folder in a different case must not double the row either — the row it would have
    /// stamped PendingCreate is a folder that already exists.
    /// </summary>
    [Fact]
    public async Task Projecting_a_case_variant_path_does_not_duplicate_the_row()
    {
        await using (var db = _harness.CreateContext())
        {
            var node = await TestProjection.Resolver(db)
                .FindOrCreateProjectedAsync(VolumeId, "PHOTOS", pendingJobId: 7, CancellationToken.None);

            node.Id.Should().Be(10);
            node.PendingState.Should().Be(EntityPendingState.None,
                "the folder is physically there — there is nothing pending on it");
        }

        await using var probe = _harness.CreateContext();
        (await probe.Directories.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// Same rule one layer up. The subtree query is case-insensitive in SQL, but the row it
    /// picks as the SUBTREE ROOT was chosen in memory with an ordinal <c>==</c> — so a job whose
    /// snapshot spelled the folder in another case found no root, returned early and cascaded
    /// nothing: the rename happened on disk while the catalog kept every old path. Reachable
    /// because a refreshed snapshot is rewritten from another job's target path, whose case is
    /// whatever the user typed, not what the scan recorded.
    /// </summary>
    [Fact]
    public async Task A_folder_rename_cascades_even_when_the_snapshot_spells_the_path_in_another_case()
    {
        using (var db = _harness.CreateContext())
        {
            db.Directories.Add(new DirectoryNode
            {
                Id = 11, VolumeId = VolumeId, ParentId = 10, Name = "Raw",
                MaterializedPath = @"Photos\Raw", IsMaterialized = true, IsPresent = true,
            });

            var job = new OperationJob
            {
                Type = JobType.RenameFolder, State = JobState.Completed, IsIntraVolume = true,
                SourceVolumeId = VolumeId, TargetVolumeId = VolumeId, TargetRelativePath = "Foto",
                SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            };
            job.Items.Add(new OperationJobItem
            {
                // The snapshot spells it "photos"; the catalog row says "Photos".
                SourceRelativePath = "photos",
                TargetRelativePath = "Foto",
                State = JobItemState.Done,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            db.OperationJobs.Add(job);
            db.SaveChanges();
        }

        await using (var db = _harness.CreateContext())
        {
            var job = await db.OperationJobs.Include(j => j.Items).SingleAsync();
            await TestProjection.Index(db).UpdateAfterCompletionAsync(job, CancellationToken.None);
        }

        await using var probe = _harness.CreateContext();
        var dirs = await probe.Directories.OrderBy(d => d.Id).ToListAsync();
        dirs.Select(d => d.MaterializedPath).Should().BeEquivalentTo(["Foto", @"Foto\Raw"],
            "the whole subtree follows the rename");
        dirs.Single(d => d.Id == 10).Name.Should().Be("Foto",
            "the renamed folder's own Name is set from the row picked as the subtree root");
    }

    /// <summary>
    /// The guard against over-correcting: NOCASE must not make two genuinely different folders
    /// look like one. <c>Photos2</c> is not <c>Photos</c>.
    /// </summary>
    [Fact]
    public async Task A_different_folder_still_gets_its_own_row()
    {
        await using (var db = _harness.CreateContext())
        {
            await TestProjection.Resolver(db)
                .FindOrCreateMaterializedAsync(VolumeId, "Photos2", CancellationToken.None);
        }

        await using var probe = _harness.CreateContext();
        var paths = await probe.Directories.Select(d => d.MaterializedPath).ToListAsync();
        paths.Should().BeEquivalentTo(["", "Photos", "Photos2"],
            "the walk also materialises the volume root on the way up");
    }
}
