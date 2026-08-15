using FileTracert.Business.Projection;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// §5: the projected path is computed at read time by walking the parents with the overlays
/// applied, so a queued folder rename or move moves its whole subtree on screen without writing
/// a single overlay on a descendant. These run against real SQLite rows, not a graph in memory.
/// </summary>
public sealed class ProjectedPathResolverTests : IDisposable
{
    private const int Vol1 = 1;
    private const int Vol2 = 2;
    private const int Root1 = 10;      // ""            on Vol1
    private const int Docs = 11;       // "Docs"        on Vol1
    private const int Sub = 12;        // "Docs\Sub"    on Vol1
    private const int Deep = 13;       // "Docs\Sub\Deep" on Vol1
    private const int Root2 = 20;      // ""            on Vol2

    private readonly SqliteInMemoryContext _harness;

    public ProjectedPathResolverTests()
    {
        _harness = new SqliteInMemoryContext();
        using var db = _harness.CreateContext();
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

        db.Volumes.AddRange(
            new Volume { Id = Vol1, VolumeGuid = @"\\?\Volume{a}\", FileSystem = "NTFS" },
            new Volume { Id = Vol2, VolumeGuid = @"\\?\Volume{b}\", FileSystem = "NTFS" });

        db.Directories.AddRange(
            new DirectoryNode { Id = Root1, VolumeId = Vol1, Name = "", MaterializedPath = "", IsMaterialized = true },
            new DirectoryNode { Id = Docs, VolumeId = Vol1, ParentId = Root1, Name = "Docs", MaterializedPath = "Docs", IsMaterialized = true },
            new DirectoryNode { Id = Sub, VolumeId = Vol1, ParentId = Docs, Name = "Sub", MaterializedPath = @"Docs\Sub", IsMaterialized = true },
            new DirectoryNode { Id = Deep, VolumeId = Vol1, ParentId = Sub, Name = "Deep", MaterializedPath = @"Docs\Sub\Deep", IsMaterialized = true },
            new DirectoryNode { Id = Root2, VolumeId = Vol2, Name = "", MaterializedPath = "", IsMaterialized = true });

        db.SaveChanges();
    }

    public void Dispose() => _harness.Dispose();

    private static CancellationToken None => CancellationToken.None;

    private async Task<IReadOnlyDictionary<int, ProjectedLocation>> ResolveAsync(params int[] ids)
    {
        await using var db = _harness.CreateContext();
        return await new ProjectedPathResolver(db, NullLogger<ProjectedPathResolver>.Instance)
            .ResolveDirectoriesAsync(ids, None);
    }

    private async Task OverlayAsync(int directoryId, Action<DirectoryNode> apply)
    {
        await using var db = _harness.CreateContext();
        var dir = await db.Directories.SingleAsync(d => d.Id == directoryId, None);
        apply(dir);
        await db.SaveChangesAsync(None);
    }

    [Fact]
    public async Task With_an_empty_queue_the_projected_path_is_the_materialized_path()
    {
        var located = await ResolveAsync(Root1, Docs, Sub, Deep);

        located[Root1].Path.Should().BeEmpty();
        located[Docs].Path.Should().Be("Docs");
        located[Sub].Path.Should().Be(@"Docs\Sub");
        located[Deep].Path.Should().Be(@"Docs\Sub\Deep");
        located[Deep].VolumeId.Should().Be(Vol1);
    }

    [Fact]
    public async Task A_queued_folder_rename_moves_its_whole_subtree_on_screen()
    {
        await OverlayAsync(Docs, d =>
        {
            d.PendingName = "Documenti";
            d.PendingState = EntityPendingState.PendingRename;
            d.PendingJobId = 1;
        });

        var located = await ResolveAsync(Docs, Sub, Deep);

        located[Docs].Path.Should().Be("Documenti");
        located[Sub].Path.Should().Be(@"Documenti\Sub");
        located[Deep].Path.Should().Be(@"Documenti\Sub\Deep",
            "descendants follow through the parent chain, with no overlay of their own");
    }

    [Fact]
    public async Task A_queued_cross_volume_folder_move_takes_the_subtree_to_the_target_volume()
    {
        await OverlayAsync(Sub, d =>
        {
            d.PendingParentId = Root2;
            d.PendingState = EntityPendingState.PendingMove;
            d.PendingJobId = 1;
        });

        var located = await ResolveAsync(Sub, Deep);

        located[Sub].Path.Should().Be("Sub");
        located[Sub].VolumeId.Should().Be(Vol2, "the projected volume is the volume of the projected parent");
        located[Deep].Path.Should().Be(@"Sub\Deep");
        located[Deep].VolumeId.Should().Be(Vol2);
    }

    [Fact]
    public async Task Two_overlays_on_the_same_chain_compose()
    {
        await OverlayAsync(Docs, d =>
        {
            d.PendingName = "Documenti";
            d.PendingState = EntityPendingState.PendingRename;
            d.PendingJobId = 1;
        });
        await OverlayAsync(Sub, d =>
        {
            d.PendingName = "Sottocartella";
            d.PendingState = EntityPendingState.PendingRename;
            d.PendingJobId = 2;
        });

        var located = await ResolveAsync(Deep);

        located[Deep].Path.Should().Be(@"Documenti\Sottocartella\Deep");
    }

    /// <summary>
    /// A pending parent pointing into its own subtree must not loop forever. The enqueue rejects
    /// the intra-volume case (C22) but not every route to it, and a read path may never hang.
    /// </summary>
    [Fact]
    public async Task A_cycle_in_the_pending_parents_is_survived_not_looped_on()
    {
        await OverlayAsync(Docs, d =>
        {
            d.PendingParentId = Deep;   // Docs re-parented under its own grandchild
            d.PendingState = EntityPendingState.PendingMove;
            d.PendingJobId = 1;
        });

        var resolve = async () => await ResolveAsync(Deep);

        var located = await resolve.Should().NotThrowAsync();
        located.Subject.Should().ContainKey(Deep);
    }

    [Fact]
    public async Task An_unknown_directory_is_simply_absent_from_the_result()
    {
        var located = await ResolveAsync(Docs, 9999);

        located.Should().ContainKey(Docs);
        located.Should().NotContainKey(9999);
    }
}
