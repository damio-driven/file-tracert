using System.Collections.Concurrent;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Host.Configuration;
using FileTracert.Tests.Business;
using FileTracert.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Host;

/// <summary>
/// The worker CLAUDE.md §3 has promised since the beginning, driven through the real host: it must
/// pick up an eligible volume on its own, apply the delta, persist the cursor, and — when the
/// cursor dies — hand the volume back to the full scan loudly and exactly once.
/// </summary>
public sealed class UsnSyncWorkerTests
{
    private const string Guid = @"\\?\Volume{55555555-5555-5555-5555-555555555555}\";
    private static readonly DateTime T = new(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Root FRN as NTFS shapes it: MFT index 5 with a sequence number on top.</summary>
    private const ulong RootFrn = (5UL << 48) | 5UL;

    private static ProbedVolume Probed => new(
        Guid, "SER-5", "Disk", "NTFS", IsRemovable: false,
        MountPoints: [@"X:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null);

    private static UsnEntry Entry(ulong frn, ulong parent, string name, string path, bool isDirectory) =>
        new(frn, parent, name, path, isDirectory,
            SizeBytes: null,
            isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
            Usn: 1);

    /// <summary>Media/ with one image, as the first full scan sees it.</summary>
    private static List<UsnEntry> Snapshot() =>
    [
        Entry(100, RootFrn, "Media", "Media", isDirectory: true),
        Entry(200, 100, "a.jpg", @"Media\a.jpg", isDirectory: false),
    ];

    private static ScriptedUsnReader Reader() => new() { Snapshot = Snapshot() };

    /// <summary>
    /// The disk as the metadata port sees it, mutable and shared: the fake hands back this very
    /// dictionary, so adding to it mid-test is how "a file appeared on disk" is expressed. Concurrent
    /// because a live worker is reading it on its own thread while the test writes.
    /// </summary>
    private static ConcurrentDictionary<string, FileMetadata> Disk(params (string Path, long Size)[] files)
    {
        var map = new ConcurrentDictionary<string, FileMetadata>();
        foreach (var (path, size) in files)
        {
            map[path] = new FileMetadata(size, T, T);
        }

        return map;
    }

    private static Func<FileTracertDbContext, CancellationToken, Task> Seed() => async (db, ct) =>
    {
        var volume = new Volume
        {
            VolumeGuid = Guid,
            Label = "Disk",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
            IsOnline = true,
        };
        db.Volumes.Add(volume);
        await db.SaveChangesAsync(ct);

        db.WatchedRoots.Add(new WatchedRoot { VolumeId = volume.Id, RelativePath = "", IsActive = true });
        await db.SaveChangesAsync(ct);
    };

    /// <summary>
    /// One create record. The incremental reader only fills the leaf name into
    /// <see cref="UsnEntry.RelativePath"/> — parents are normally outside the delta — so the
    /// fixture is built the same way.
    /// </summary>
    private static UsnChangeRecord Created(ulong frn, ulong parent, string name, long usn) =>
        new(Entry(frn, parent, name, name, isDirectory: false) with { Usn = usn },
            UsnReason.FileCreate | UsnReason.Close,
            IsRename: false,
            OldName: null);

    private static async Task WaitForFirstScanAsync(FileTracertAppFactory factory) =>
        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var volume = await db.Volumes.SingleAsync();
            return volume.LastFullScanUtc is not null && volume.LastUsn == 500;
        });

    [Fact]
    public async Task Worker_applies_a_delta_and_persists_the_cursor()
    {
        var reader = Reader();
        var disk = Disk((@"Media\a.jpg", 11));

        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            Probe = new FakeVolumesProbe([Probed]),
            UsnReader = reader,
            MetadataReader = new FakeFileMetadataReader(disk),
            Seed = Seed(),
        };

        using var _ = factory.CreateClient();

        // The full scan goes first and leaves the cursor behind — a delta has no meaning without a
        // checkpoint to resume from, which is what the column added this step exists for.
        await WaitForFirstScanAsync(factory);

        // Now work happens outside the application, and only the journal knows about it: the
        // snapshot a scan would read is deliberately left alone, so a file that appears in the
        // catalog can only have come through the delta.
        disk[@"Media\new.jpg"] = new FileMetadata(44, T, T);
        reader.Script([Created(201, 100, "new.jpg", usn: 600)], nextUsn: 900);

        // Waiting on the CURSOR and not on the row, and the difference is not cosmetic. The cursor
        // is written LAST, in a transaction of its own (14d), so a tick that has committed the file
        // row has not necessarily finished: waiting on the row and then reading LastUsn is a race
        // the test loses on a loaded machine — and it loses it with exactly the message a torn
        // fixture produces, "LastUsn one increment behind", which is how one symptom came to have
        // two causes. The cursor is the one point at which everything asserted below is settled.
        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            return (await db.Volumes.SingleAsync()).LastUsn == 900;
        });

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<FileTracertDbContext>();

        var indexed = await verifyDb.Files.SingleAsync(f => f.Name == "new.jpg");
        indexed.SizeBytes.Should().Be(44);
        indexed.UsnFileRef.Should().Be(201);
        indexed.IsIncluded.Should().BeTrue();
        indexed.IsPresent.Should().BeTrue();

        // The cursor is what the wait above settled on, so this line restates the property rather
        // than discovering it — a tick that never wrote it fails as a TimeoutException there, which
        // is still a red and a clearer one. The journal id beside it is not restated anywhere: a
        // position without the instance it belongs to is not a cursor (§4).
        var volume = await verifyDb.Volumes.SingleAsync();
        volume.LastUsn.Should().Be(900, "the cursor moves only once the delta is applied");
        volume.UsnJournalId.Should().Be(7);

        // …and the row the scan wrote is untouched: a delta must never be read as a statement
        // about everything it did not mention.
        var untouched = await verifyDb.Files.SingleAsync(f => f.Name == "a.jpg");
        untouched.IsPresent.Should().BeTrue();
        untouched.IsIncluded.Should().BeTrue();
        (await verifyDb.Files.CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// The cursor lives in the database, not in the worker, so a host that starts on an existing
    /// catalog resumes where the previous one stopped. Two hosts over ONE database file, because
    /// nothing weaker actually proves it.
    /// </summary>
    [Fact]
    public async Task A_new_host_resumes_from_the_persisted_cursor()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"ft-test-{System.Guid.NewGuid():N}.db");
        var logPath = DatabaseLocation.ResolveLogs(databasePath);
        var disk = Disk((@"Media\a.jpg", 11));

        try
        {
            var first = Reader();
            using (var factory = new FileTracertAppFactory(databasePath)
            {
                KeepDatabaseOnDispose = true,
                DisableVolumeSync = true,
                Probe = new FakeVolumesProbe([Probed]),
                UsnReader = first,
                MetadataReader = new FakeFileMetadataReader(disk),
                Seed = Seed(),
            })
            {
                using var _ = factory.CreateClient();
                await WaitForFirstScanAsync(factory);

                disk[@"Media\one.jpg"] = new FileMetadata(1, T, T);
                first.Script([Created(201, 100, "one.jpg", usn: 600)], nextUsn: 700);

                await TestPolling.WaitUntilAsync(async () =>
                {
                    using var scope = factory.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
                    return (await db.Volumes.SingleAsync()).LastUsn == 700;
                });
            }

            // Second host, same catalog, no seeding: everything it knows it reads off the disk.
            var second = Reader();
            second.Script([Created(202, 100, "two.jpg", usn: 800)], nextUsn: 1100);
            disk[@"Media\two.jpg"] = new FileMetadata(2, T, T);

            using (var factory = new FileTracertAppFactory(databasePath)
            {
                DisableVolumeSync = true,
                DisableScan = true, // a full scan here would hide the very thing under test
                Probe = new FakeVolumesProbe([Probed]),
                UsnReader = second,
                MetadataReader = new FakeFileMetadataReader(disk),
            })
            {
                using var _ = factory.CreateClient();

                // The cursor again, for the reason given in the first case: the row lands one
                // transaction before it, and this is the assertion that read 700 on a loaded
                // machine while `two.jpg` was already in the catalog.
                await TestPolling.WaitUntilAsync(async () =>
                {
                    using var scope = factory.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
                    return (await db.Volumes.SingleAsync()).LastUsn == 1100;
                });

                using var verify = factory.Services.CreateScope();
                var verifyDb = verify.ServiceProvider.GetRequiredService<FileTracertDbContext>();

                // Resumed from 700 — what the FIRST host wrote — and not from anything this host
                // could have known on its own.
                second.Resumed.Should().NotBeEmpty();
                second.Resumed[0].Should().Be((700L, 7ul));
                (await verifyDb.Volumes.SingleAsync()).LastUsn.Should().Be(1100);

                // And the previous host's work is still there, not re-derived by a fresh scan.
                (await verifyDb.Files.Select(f => f.Name).ToListAsync())
                    .Should().BeEquivalentTo("a.jpg", "one.jpg", "two.jpg");
            }
        }
        finally
        {
            SqliteTestDatabase.Delete(databasePath, logPath);
        }
    }

    /// <summary>
    /// A journal that no longer covers our position is the one case that must be loud: the index
    /// goes silently stale otherwise. Once per invalidation, though — the applier drops the cursor,
    /// so the volume stops being eligible until a full scan writes a new one, instead of raising
    /// the same warning on every tick.
    /// </summary>
    [Fact]
    public async Task A_dead_cursor_asks_for_a_full_scan_once_and_says_so()
    {
        var reader = Reader();
        var disk = Disk((@"Media\a.jpg", 11));

        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            Probe = new FakeVolumesProbe([Probed]),
            UsnReader = reader,
            MetadataReader = new FakeFileMetadataReader(disk),
            Seed = Seed(),
        };

        using var _ = factory.CreateClient();
        await WaitForFirstScanAsync(factory);

        // The journal was deleted and recreated: same volume, brand-new numbering.
        reader.JournalId = 4242;

        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            return await db.Notifications.AnyAsync(n => n.Source == "Scan");
        });

        // The requested full scan re-establishes a cursor under the new id. Waited for, not slept
        // through: a fixed slice of wall clock is "several cycles" only on a machine that is not
        // busy, and this assertion was one of the ones that reddened on a loaded one.
        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var v = await db.Volumes.SingleAsync();
            return v.UsnJournalId == 4242 && v.LastUsn == 500;
        });

        // …and only THEN several more cycles, because what must not repeat is the WARNING, not the
        // reading — and that is a claim about time passing with nothing being written.
        await Task.Delay(2500);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<FileTracertDbContext>();

        var notification = await verifyDb.Notifications.SingleAsync(n => n.Source == "Scan");
        notification.Severity.Should().Be(NotificationSeverity.Warning);
        notification.VolumeId.Should().NotBeNull();
        notification.Message.Should().NotBeNullOrWhiteSpace();

        // The recovery is a real one, not just a quiet one: the volume is back on the short road,
        // reading the journal it actually has now.
        var volume = await verifyDb.Volumes.SingleAsync();
        volume.UsnJournalId.Should().Be(4242);
        volume.LastUsn.Should().Be(500);
        reader.Resumed.Should().Contain((500L, 4242ul));
    }

    /// <summary>
    /// The worker's pre-filter asks the same question the applier asks: <b>is there a cursor?</b>
    /// Not <em>which engine walked</em> — that question stopped being the right one at A4, when a
    /// watched subfolder began being walked by enumeration and checkpointing the journal anyway.
    ///
    /// <para>This test used to assert the opposite, and that is how the miss survived: step 19
    /// rewrote the gate in <c>UsnDeltaApplier</c> and in the harness scenarios, left the worker's
    /// SQL pre-filter asking about <c>ScanEngine</c>, and this test pinned the stale rule instead
    /// of catching it. On the real catalog the consequence was silent and total — the system
    /// volume got its cursor from the hybrid and no delta ever read it, so the incremental path
    /// the user asked for was inert while looking installed.</para>
    ///
    /// <para>Two volumes on purpose, and only one of them holds a cursor. "The journal was never
    /// opened" is also what a worker that is not running looks like — but here the enumeration-
    /// walked volume is the ONLY thing that could have been opened, so a single read proves both
    /// that the worker is alive and that it served exactly the shape the hybrid produces. The
    /// other volume has no checkpoint at all: nothing to resume from, and opening its journal
    /// would be a volume handle and a syscall per cycle, for ever, for nothing.</para>
    ///
    /// <para>The two are NOT told apart by the cursor they resume from, which is the trap the
    /// previous version of this test wrote a warning about and then walked into anyway: an empty
    /// delta checkpoints <c>NextUsn</c>, so after one cycle every volume resumes from the same
    /// number the fake reports.</para>
    /// </summary>
    [Fact]
    public async Task A_cursor_is_served_whichever_engine_walked_and_a_volume_without_one_is_not()
    {
        const string eligibleGuid = @"\\?\Volume{56565656-5656-5656-5656-565656565656}\";
        const string uncheckpointedGuid = @"\\?\Volume{57575757-5757-5757-5757-575757575757}\";

        // The journal's tail must sit AT the eligible volume's cursor, not below it: an empty
        // delta checkpoints NextUsn, and a fake whose tail runs backwards would drag the cursor
        // back and make every later cycle unrecognisable.
        var reader = Reader();
        reader.Script([], nextUsn: 777);

        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Probe = new FakeVolumesProbe(
            [
                Probed,
                new ProbedVolume(eligibleGuid, "SER-6", "Other", "NTFS", IsRemovable: false,
                    MountPoints: [@"Y:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null),
                new ProbedVolume(uncheckpointedGuid, "SER-7", "Third", "NTFS", IsRemovable: false,
                    MountPoints: [@"Z:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null),
            ]),
            UsnReader = reader,
            MetadataReader = new FakeFileMetadataReader(Disk((@"Media\a.jpg", 11))),
            Seed = async (db, ct) =>
            {
                // Walked by ENUMERATION and holding a cursor — the shape the hybrid produces for
                // every volume whose watched roots are subfolders. A scan writes a cursor only
                // when the walk captured identities, so this volume has rows the delta can place.
                var hybrid = new Volume
                {
                    VolumeGuid = eligibleGuid,
                    FileSystem = "NTFS",
                    ScanEngine = VolumeScanEngine.Enumeration,
                    IsOnline = true,
                    LastFullScanUtc = T,
                    LastUsn = 777,
                    UsnJournalId = 7,
                };

                // No cursor at all: nothing to resume from, so its journal must never be opened —
                // and, being the only other volume, it is what makes the read below unambiguous.
                var noCursor = new Volume
                {
                    VolumeGuid = Guid,
                    FileSystem = "NTFS",
                    ScanEngine = VolumeScanEngine.UsnJournal,
                    IsOnline = true,
                    LastFullScanUtc = T,
                    LastUsn = null,
                    UsnJournalId = null,
                };

                db.Volumes.AddRange(hybrid, noCursor);
                await db.SaveChangesAsync(ct);

                db.WatchedRoots.Add(new WatchedRoot { VolumeId = hybrid.Id, RelativePath = "", IsActive = true });
                db.WatchedRoots.Add(new WatchedRoot { VolumeId = noCursor.Id, RelativePath = "", IsActive = true });
                await db.SaveChangesAsync(ct);
            },
        };

        using var _ = factory.CreateClient();

        await Task.Delay(2500);

        // The premise, asserted rather than assumed: this volume must still be the enumeration-
        // walked one when the worker looked at it, or the assertion below proves nothing.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var stored = await db.Volumes.SingleAsync(v => v.VolumeGuid == eligibleGuid);
            stored.ScanEngine.Should().Be(VolumeScanEngine.Enumeration);
        }

        reader.Resumed.Should().NotBeEmpty(
            "a cursor written by the hybrid's enumeration walk is a cursor and must be served — " +
            "and it is the only volume here that has one, so nothing else could have been read");
        reader.Resumed.Should().OnlyContain(r => r.JournalId == 7);
    }
}
