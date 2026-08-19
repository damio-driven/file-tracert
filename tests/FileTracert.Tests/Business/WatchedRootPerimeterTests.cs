using FileTracert.Business.Setup;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Business;

/// <summary>
/// Switching a watched root off and on is a PERIMETER change, and §4 says a perimeter change is
/// recorded on <c>IsIncluded</c> and costs no re-scan. Against the real
/// <see cref="WatchedRootsService"/> + <see cref="FilterReconciler"/> + real SQLite: no disk is
/// read here at all, which is the point — the rows come back the moment the user says so.
/// </summary>
public sealed class WatchedRootPerimeterTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness = new();

    public void Dispose() => _harness.Dispose();

    private const string Guid = @"\\?\Volume{44444444-4444-4444-4444-444444444444}\";

    private WatchedRootsService NewService(FileTracertDbContext db) => new(db, new FilterReconciler(db));

    private async Task<(int RootId, int JpgId, int PngId)> SeedAsync()
    {
        await using var db = _harness.CreateContext();
        db.AppSettings.RemoveRange(db.AppSettings);
        db.AppSettings.Add(new AppSettings
        {
            DefaultExtensionFilter = ["jpg"], ExcludedPaths = [], ApiToken = "token", SpaceMarginPercent = 5,
        });

        var volume = new Volume
        {
            VolumeGuid = Guid, FileSystem = "NTFS", ScanEngine = VolumeScanEngine.UsnJournal, IsOnline = true,
        };
        db.Volumes.Add(volume);
        await db.SaveChangesAsync();

        var root = new DirectoryNode
        {
            VolumeId = volume.Id, Name = "", MaterializedPath = "", IsMaterialized = true,
        };
        db.Directories.Add(root);
        await db.SaveChangesAsync();

        var photos = new DirectoryNode
        {
            VolumeId = volume.Id, ParentId = root.Id, Name = "Photos", MaterializedPath = "Photos",
            IsMaterialized = true,
        };
        db.Directories.Add(photos);
        await db.SaveChangesAsync();

        var watched = new WatchedRoot { VolumeId = volume.Id, RelativePath = "Photos", IsActive = true };
        db.WatchedRoots.Add(watched);

        var jpg = NewFile(volume.Id, photos.Id, "a.jpg", "jpg");
        var png = NewFile(volume.Id, photos.Id, "b.png", "png");
        png.IsIncluded = false; // the type filter already excluded it; nothing here may re-include it
        db.Files.AddRange(jpg, png);
        await db.SaveChangesAsync();

        return (watched.Id, jpg.Id, png.Id);
    }

    private static FileEntry NewFile(int volumeId, int directoryId, string name, string extension) => new()
    {
        VolumeId = volumeId, DirectoryId = directoryId, Name = name, Extension = extension,
        Category = FileCategory.Image, SizeBytes = 10, IsIncluded = true, IsPresent = true,
        LastIndexedUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task Switching_a_root_off_excludes_its_rows_and_leaves_them_present()
    {
        var (rootId, jpgId, _) = await SeedAsync();

        await using (var db = _harness.CreateContext())
        {
            var (_, reconcile) = await NewService(db).UpdateAsync(
                rootId, new UpdateWatchedRootRequest(IsActive: false, FilterOverride: null), CancellationToken.None);

            reconcile.Should().NotBeNull();
            reconcile!.ExcludedCount.Should().Be(2);
            reconcile.NeedsScan.Should().BeFalse("nothing new can appear by narrowing the perimeter");
        }

        await using var read = _harness.CreateContext();
        var jpg = await read.Files.SingleAsync(f => f.Id == jpgId);
        jpg.IsIncluded.Should().BeFalse();
        jpg.IsPresent.Should().BeTrue("the files did not move; the perimeter did");
    }

    [Fact]
    public async Task Switching_a_root_back_on_re_includes_its_rows_without_a_scan()
    {
        var (rootId, jpgId, pngId) = await SeedAsync();

        await using (var db = _harness.CreateContext())
        {
            await NewService(db).UpdateAsync(
                rootId, new UpdateWatchedRootRequest(IsActive: false, FilterOverride: null), CancellationToken.None);
        }

        await using (var db = _harness.CreateContext())
        {
            var (_, reconcile) = await NewService(db).UpdateAsync(
                rootId, new UpdateWatchedRootRequest(IsActive: true, FilterOverride: null), CancellationToken.None);

            reconcile.Should().NotBeNull();
            reconcile!.IncludedCount.Should().Be(1);
            reconcile.NeedsScan.Should().BeTrue(
                "what was never indexed while the root was off cannot be un-excluded from the catalog");
        }

        await using var read = _harness.CreateContext();
        (await read.Files.SingleAsync(f => f.Id == jpgId)).IsIncluded.Should().BeTrue();
        (await read.Files.SingleAsync(f => f.Id == pngId)).IsIncluded
            .Should().BeFalse("the TYPE filter still rejects it — the perimeter is not the only gate");
    }

    [Fact]
    public async Task A_request_that_changes_nothing_reconciles_nothing()
    {
        var (rootId, _, _) = await SeedAsync();

        await using var db = _harness.CreateContext();
        var (_, reconcile) = await NewService(db).UpdateAsync(
            rootId, new UpdateWatchedRootRequest(IsActive: true, FilterOverride: null), CancellationToken.None);

        reconcile.Should().BeNull("the root was already active: a no-op must not rewrite the index");
    }
}
