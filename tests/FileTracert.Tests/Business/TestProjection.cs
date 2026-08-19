using FileTracert.Business.Filtering;
using FileTracert.Business.Operations;
using FileTracert.Business.Projection;
using FileTracert.Business.Realtime;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Realtime;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Builds the projection collaborators the queue services depend on, against the real
/// implementations. Exists so the ~20 hand-wired service constructions in these tests state
/// what the test is about instead of repeating the same four arguments.
/// </summary>
internal static class TestProjection
{
    public static DirectoryResolver Resolver(FileTracertDbContext db) => new(db);

    public static OverlayWriter Overlay(FileTracertDbContext db, IFileSearchIndex? fts = null) =>
        new(db, new DirectoryResolver(db), fts ?? new FakeFileSearchIndex(),
            NullLogger<OverlayWriter>.Instance);

    /// <summary>The real enqueue guard — never a stub: the tests are largely ABOUT what it decides.</summary>
    public static PendingWorkGuard Guard(FileTracertDbContext db) => new(db);

    /// <summary>The real Blocked-job revaluation, over the real ledger.</summary>
    public static BlockedJobRevaluator Revaluator(
        FileTracertDbContext db, ISpaceLedger ledger, IFileSearchIndex? fts = null,
        IVolumeProbe? probe = null) =>
        new(db, ledger, Space(db, ledger, probe), Unblocker(db, fts), TestProjection.Realtime(),
            NullLogger<BlockedJobRevaluator>.Instance);

    /// <summary>
    /// The real "does it fit?" service. The default platform probe echoes the volume row's
    /// <c>FreeBytesLastKnown</c>, so a test that is not about the live probe keeps arranging space
    /// the way it always has; a test that IS about it passes a <see cref="StubFreeSpaceProbe"/>
    /// reporting whatever the disk really holds.
    /// </summary>
    public static SpaceCheck Space(
        FileTracertDbContext db, ISpaceLedger ledger, IVolumeProbe? probe = null) =>
        new(db, ledger, probe ?? new LastKnownFreeSpaceProbe(db), NullLogger<SpaceCheck>.Instance);

    /// <summary>The real release path: guard re-ask + snapshot refresh + overlay.</summary>
    public static JobUnblocker Unblocker(FileTracertDbContext db, IFileSearchIndex? fts = null) =>
        new(db, Guard(db), Overlay(db, fts),
            new JobSnapshotRefresher(db, NullLogger<JobSnapshotRefresher>.Instance),
            NullLogger<JobUnblocker>.Instance);

    /// <summary>
    /// The real guarded gateway over a publisher of the test's choosing (a no-op by default).
    /// Never a stub of <see cref="RealtimeEvents"/> itself: its catch is part of what is tested.
    /// </summary>
    public static RealtimeEvents Realtime(IRealtimePublisher? publisher = null) =>
        new(publisher ?? new NullRealtimePublisher(), NullLogger<RealtimeEvents>.Instance);

    public static IndexUpdater Index(FileTracertDbContext db, IFileSearchIndex? fts = null) =>
        new(db, fts ?? new FakeFileSearchIndex(), new DirectoryResolver(db), Filters(db),
            NullLogger<IndexUpdater>.Instance);

    /// <summary>The real filter resolution — a rename asks it whether the new name is still included.</summary>
    public static RootFilterResolver Filters(FileTracertDbContext db) =>
        new(db, NullLogger<RootFilterResolver>.Instance);
}
