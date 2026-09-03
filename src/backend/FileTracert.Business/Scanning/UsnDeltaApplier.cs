using System.ComponentModel;
using FileTracert.Business.Filtering;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Scanning;

/// <summary>Why an incremental pass did or did not happen.</summary>
public enum UsnSyncStatus
{
    /// <summary>The volume cannot be synced incrementally right now (see the reason).</summary>
    NotEligible,

    /// <summary>The journal had nothing new; the cursor was moved forward and nothing else.</summary>
    UpToDate,

    /// <summary>A delta was read and applied to the index.</summary>
    Applied,

    /// <summary>
    /// The cursor is dead (journal recreated, or trimmed past our position). The delta was NOT
    /// applied and a full scan is the only way back.
    /// </summary>
    RescanRequired,
}

/// <param name="Indexed">Files merged into the index by this delta.</param>
/// <param name="MarkedAbsent">Rows the delta proved are no longer at their indexed location.</param>
/// <param name="Excluded">Rows the delta moved outside the user's perimeter.</param>
/// <param name="DirectoriesTouched">Directory rows inserted or refreshed.</param>
/// <param name="Unresolved">
/// Records whose path could not be placed inside the catalog. Not an error: the overwhelming
/// majority of a volume's journal traffic happens in folders this catalog has never indexed.
/// </param>
public sealed record UsnSyncResult(
    UsnSyncStatus Status,
    string Reason,
    int Indexed = 0,
    int MarkedAbsent = 0,
    int Excluded = 0,
    int DirectoriesTouched = 0,
    int Unresolved = 0);

/// <summary>
/// The short road into the index: reads what the NTFS change journal recorded since the volume's
/// last checkpoint and applies just that, instead of walking the MFT again (CLAUDE.md §1.2, §4).
///
/// <para><b>The delta and the full scan must agree.</b> Everything durable here goes through the
/// pieces the scan uses — <see cref="IBulkIndexWriter.MergeScannedFilesAsync"/> for the upsert,
/// <see cref="DirectoryMerger.EnsureAsync"/> for the tree, <see cref="ScanPerimeter"/> and
/// <see cref="FileFilter"/> for the verdict — so a row written by a tick and the same row written
/// by a scan are the same row. The one thing the delta may never borrow is the scan's ABSENCE
/// pass: that pass reads "not touched by this run" as "not on disk", which is true of a scan
/// (it looked everywhere) and false of a delta (it looked at what changed).</para>
///
/// <para><b>Absence is only ever written against evidence.</b> A row is flagged
/// <c>IsPresent = false</c> when the journal says the file was deleted, or when it says the file
/// is no longer at the location the row records. Never because the delta did not mention it.
/// Exclusions keep the step 11g/11h semantics: outside the perimeter is
/// <c>IsIncluded = false</c> with the cause that is true of it, and presence is left alone.</para>
///
/// <para><b>Crash safety.</b> The cursor is written LAST, in its own transaction, exactly like the
/// scan's checkpoint (step 9a). Everything the delta does is idempotent — the merge matches on the
/// FRN, the flags are set to constants — so an interrupted tick simply re-reads the same delta and
/// converges. The opposite order would let a crash declare a delta consumed that was never
/// applied, and nothing would ever go back for it.</para>
/// </summary>
public sealed class UsnDeltaApplier
{
    private readonly FileTracertDbContext _db;
    private readonly IVolumeProbe _probe;
    private readonly IUsnReader _usnReader;
    private readonly IFileMetadataReader _metadataReader;
    private readonly IBulkIndexWriter _bulkWriter;
    private readonly DirectoryMerger _directoryMerger;
    private readonly IFileSearchIndex _ftsIndex;
    private readonly ILogger<UsnDeltaApplier> _logger;

    /// <summary>
    /// How many rows travel per statement/transaction. Same reasoning as the scan's batch size:
    /// small enough that no other writer waits long for SQLite's single write lock, big enough
    /// that a normal delta fits in one round trip.
    /// </summary>
    public int BatchSize { get; init; } = 500;

    public UsnDeltaApplier(
        FileTracertDbContext db,
        IVolumeProbe probe,
        IUsnReader usnReader,
        IFileMetadataReader metadataReader,
        IBulkIndexWriter bulkWriter,
        DirectoryMerger directoryMerger,
        IFileSearchIndex ftsIndex,
        ILogger<UsnDeltaApplier> logger)
    {
        _db = db;
        _probe = probe;
        _usnReader = usnReader;
        _metadataReader = metadataReader;
        _bulkWriter = bulkWriter;
        _directoryMerger = directoryMerger;
        _ftsIndex = ftsIndex;
        _logger = logger;
    }

    public async Task<UsnSyncResult> SyncVolumeAsync(int volumeId, CancellationToken ct)
    {
        var volume = await _db.Volumes.FirstOrDefaultAsync(v => v.Id == volumeId, ct);
        if (volume is null)
        {
            return new UsnSyncResult(UsnSyncStatus.NotEligible, "the volume is not in the catalog");
        }

        if (Ineligible(volume) is { } why)
        {
            return new UsnSyncResult(UsnSyncStatus.NotEligible, why);
        }

        var probed = _probe.TryGetByGuid(volume.VolumeGuid);
        if (probed?.MountPoints.FirstOrDefault() is not { } mountRoot)
        {
            return new UsnSyncResult(UsnSyncStatus.NotEligible, "the volume is not mounted right now");
        }

        var roots = await _db.WatchedRoots
            .Where(r => r.VolumeId == volumeId && r.IsActive)
            .ToListAsync(ct);
        if (roots.Count == 0)
        {
            return new UsnSyncResult(UsnSyncStatus.NotEligible, "the volume has no active watched roots");
        }

        UsnChangeResult delta;
        try
        {
            delta = _usnReader.ReadChanges(
                volume.VolumeGuid, volume.LastUsn!.Value, unchecked((ulong)volume.UsnJournalId!.Value), ct);
        }
        catch (Win32Exception ex)
        {
            // Resilience, not silence (§9): the volume handle was refused, or the journal is
            // momentarily unreadable. The cursor is deliberately KEPT — nothing here says our
            // position is gone, only that we could not look, and throwing a valid cursor away
            // would sentence a multi-million-row volume to a full re-scan for a transient failure.
            // The next cycle simply tries again; the warning is what makes a persistent failure
            // visible.
            _logger.LogWarning(
                ex,
                "Journal read failed for volume {VolumeId} ({Guid}); the cursor is kept and the next cycle will retry.",
                volumeId, volume.VolumeGuid);
            return new UsnSyncResult(
                UsnSyncStatus.NotEligible,
                $"the change journal could not be read: {ex.Message} (code {ex.NativeErrorCode})");
        }

        if (delta.RequiresFullRescan)
        {
            await InvalidateCursorAsync(volume, ct);
            return new UsnSyncResult(
                UsnSyncStatus.RescanRequired,
                "the change journal no longer covers the position this volume was left at");
        }

        if (delta.Changes.Count == 0)
        {
            await CheckpointAsync(volume, delta.NextUsn, ct);
            return new UsnSyncResult(UsnSyncStatus.UpToDate, "no changes since the last pass");
        }

        var result = await ApplyAsync(volume, mountRoot, roots, delta, ct);

        // Last, and only now: everything above is committed, so a crash before this line costs a
        // repeated (idempotent) delta and never a skipped one.
        await CheckpointAsync(volume, delta.NextUsn, ct);
        return result;
    }

    /// <summary>
    /// Why this volume cannot take the short road, phrased for a log line. All four conditions are
    /// about the SAME thing: the incremental path resumes a journal read that a previous full scan
    /// started, and it re-places files by walking directory FRNs that only a USN scan writes. An
    /// enumeration scan leaves those null, so a delta on top of it could not resolve a single path.
    /// </summary>
    private static string? Ineligible(Volume volume) =>
        !volume.IsCatalogable ? "the volume is excluded from cataloguing"
        : VolumeMapper.EngineFor(volume.FileSystem) != VolumeScanEngine.UsnJournal
            ? $"the filesystem ({volume.FileSystem}) has no change journal"
        : volume.ScanEngine != VolumeScanEngine.UsnJournal
            ? "the last full scan used enumeration, so the directory rows carry no file references"
        : volume.LastFullScanUtc is null ? "the volume has never been fully scanned"
        : volume.LastUsn is null || volume.UsnJournalId is null ? "the volume has no journal checkpoint"
        : null;

    // ── the pass itself ───────────────────────────────────────────────────────

    private async Task<UsnSyncResult> ApplyAsync(
        Volume volume,
        string mountRoot,
        List<WatchedRoot> roots,
        UsnChangeResult delta,
        CancellationToken ct)
    {
        var settings = await _db.AppSettings.FirstOrDefaultAsync(ct);
        var categoryMap = await _db.ExtensionCategories.ToDictionaryAsync(e => e.Extension, e => e.Category, ct);
        var filters = await RootFilters.ResolveAsync(settings, roots, (root, ex) =>
        {
            // Logged in full, but no Notification: this runs every few seconds, and the full scan
            // is the authority that already told the user about this same malformed override.
            _logger.LogWarning(
                ex,
                "Invalid filter override for root '{Root}' on volume {VolumeId}; using the default filter.",
                root.RelativePath, volume.Id);
            return Task.CompletedTask;
        }, ct);

        var (deleted, alive) = Coalesce(delta.Changes);
        var placed = await PlaceAsync(volume.Id, alive, ct);

        var perimeter = new ScanPerimeter(filters.Keys);
        var (keptDirs, indexable, outside) = Classify(placed, perimeter, filters, categoryMap);

        var ensured = await _directoryMerger.EnsureAsync(volume.Id, keptDirs, BatchSize, ct);
        var directoryIdByPath = ensured.IdByPath;

        var indexed = await MergeFilesAsync(volume.Id, mountRoot, indexable, directoryIdByPath, ct);
        var (absent, excluded) = await ReconcileAsync(volume.Id, deleted, outside, directoryIdByPath, perimeter, ct);

        _logger.LogInformation(
            "Volume {VolumeId} USN delta: {Records} record(s) -> {Indexed} indexed, {Absent} absent, " +
            "{Excluded} excluded, {Dirs} director(ies), {Unresolved} outside the catalog.",
            volume.Id, delta.Changes.Count, indexed, absent, excluded,
            ensured.Inserted + ensured.Revived, placed.Unresolved);

        return new UsnSyncResult(
            UsnSyncStatus.Applied,
            $"{delta.Changes.Count} journal record(s)",
            indexed, absent, excluded, ensured.Inserted + ensured.Revived, placed.Unresolved);
    }

    /// <summary>
    /// One entry per FRN, keeping the LAST record for it. The journal is ordered, so the last
    /// record carries the name and parent the object ended up with — which is what makes a rename
    /// (an old-name record followed by a new-name one) collapse into "this file is now called
    /// that", with no rename bookkeeping of our own.
    /// <para>Deletion is decided from that last record and not from the union of the reasons:
    /// a file created and deleted inside one window ends on a delete, while an MFT record reused
    /// by a new file ends on a create — the union would call both deletions.</para>
    /// </summary>
    private static (List<ulong> Deleted, List<UsnEntry> Alive) Coalesce(IReadOnlyList<UsnChangeRecord> changes)
    {
        var last = new Dictionary<ulong, UsnChangeRecord>(changes.Count);
        foreach (var record in changes)
        {
            var frn = record.Entry.FileReferenceNumber;

            // The volume root is not a row this pass owns: DirectoryMerger creates it and nothing
            // that happens to it can move it.
            if (!FrnUtil.IsRoot(frn))
            {
                last[frn] = record;
            }
        }

        var deleted = new List<ulong>();
        var alive = new List<UsnEntry>(last.Count);
        foreach (var (frn, record) in last)
        {
            if ((record.Reason & UsnReason.FileDelete) != 0)
            {
                deleted.Add(frn);
            }
            else
            {
                alive.Add(record.Entry);
            }
        }

        return (deleted, alive);
    }

    /// <summary>
    /// Gives every surviving record a place in the catalog's coordinates: the relative path it now
    /// has, and the identity of the row that already describes it (if any).
    ///
    /// <para>Paths are rebuilt from the PARENT's FRN plus the record's own name, through the same
    /// <see cref="UsnPathResolver"/> the full snapshot uses. Its map holds the directories inside
    /// this delta (so a freshly created chain resolves against itself) and its fallback answers
    /// from the catalog's directory rows. A parent that is in neither is a directory this catalog
    /// has never indexed — that is not a failure, it is how the delta inherits the scan's subtree
    /// exclusions (C16) without reading a byte off the disk.</para>
    /// </summary>
    private async Task<PlacedDelta> PlaceAsync(int volumeId, List<UsnEntry> alive, CancellationToken ct)
    {
        var deltaDirectories = new Dictionary<ulong, FrnNode>();
        foreach (var entry in alive.Where(e => e.IsDirectory))
        {
            deltaDirectories[entry.FileReferenceNumber] =
                new FrnNode(entry.Name, entry.ParentFileReferenceNumber, IsDirectory: true);
        }

        var wantedParents = new HashSet<ulong>();
        foreach (var entry in alive)
        {
            var parent = entry.ParentFileReferenceNumber;
            if (!deltaDirectories.ContainsKey(parent) && !FrnUtil.IsRoot(parent))
            {
                wantedParents.Add(parent);
            }
        }

        var catalogDirectories = await LoadDirectoriesByFrnAsync(volumeId, wantedParents, ct);
        var catalogFiles = await LoadFilesByFrnAsync(volumeId, alive.Select(e => e.FileReferenceNumber), ct);

        // FrnUtil.IsRoot is what answers for the volume root: the root's FRN carries a sequence
        // number in its high bits, so it is never literally 5, and FSCTL_ENUM_USN_DATA does not
        // reliably hand out the root's own record for the scan to have persisted it.
        var resolver = new UsnPathResolver(
            deltaDirectories,
            FrnUtil.RootMftIndex,
            frn => FrnUtil.IsRoot(frn) ? string.Empty
                : catalogDirectories.TryGetValue(frn, out var dir) ? dir.Path
                : null);

        var items = new List<PlacedItem>(alive.Count);
        var unresolved = 0;

        foreach (var entry in alive)
        {
            ct.ThrowIfCancellationRequested();

            if (!resolver.TryResolve(entry.ParentFileReferenceNumber, out var parentPath))
            {
                unresolved++;
                continue;
            }

            int? parentId = catalogDirectories.TryGetValue(entry.ParentFileReferenceNumber, out var parentRow)
                ? parentRow.Id
                : null;

            catalogFiles.TryGetValue(entry.FileReferenceNumber, out var existing);
            items.Add(new PlacedItem(entry, ScanPath.Join(parentPath, entry.Name), parentPath, parentId, existing));
        }

        var inherited = catalogDirectories.Values
            .Where(d => d.ExcludedByScan && d.Path.Length > 0)
            .Select(d => d.Path)
            .ToList();

        return new PlacedDelta(items, unresolved, inherited);
    }

    /// <summary>
    /// Splits the placed records the way the scan's gather does, using the same two halves of the
    /// filter and the same <see cref="ScanPerimeter"/>, so both paths reach identical verdicts.
    /// </summary>
    private static (List<ScannedDirectory> Directories, List<PlacedItem> Indexable, List<PlacedItem> Outside)
        Classify(
            PlacedDelta placed,
            ScanPerimeter perimeter,
            IReadOnlyDictionary<string, EffectiveFilter> filters,
            IReadOnlyDictionary<string, FileCategory> categoryMap)
    {
        var directories = new List<ScannedDirectory>();
        var indexable = new List<PlacedItem>();
        var outside = new List<PlacedItem>();

        // Step 18: what the catalog remembers about the parents comes first, so the walk below
        // sees a hidden ancestor the records do not mention. Inherited: the rows under those
        // folders were stamped by the tick that saw them go hidden (ExcludeSubtreesAsync), and the
        // subtree pass must not pay that walk again for every file written inside since.
        foreach (var path in placed.InheritedExclusions)
        {
            perimeter.ExcludeSubtree(path, PerimeterVerdict.HiddenAncestor, inherited: true);
        }

        foreach (var item in placed.Items.Where(i => i.Entry.IsDirectory))
        {
            if (item.Path.Length == 0)
            {
                continue; // the volume root, which this pass does not own
            }

            var root = perimeter.GoverningRoot(item.Path);
            if (root is null)
            {
                continue; // outside every active root: nothing to insert, nothing to say
            }

            var verdict = FileFilter.EvaluatePerimeter(item.Path, item.Entry.Attributes, filters[root]);
            if (verdict.IsInside)
            {
                directories.Add(new ScannedDirectory(item.Path, unchecked((long)item.Entry.FileReferenceNumber)));
            }
            else
            {
                perimeter.ExcludeSubtree(item.Path, verdict);
            }
        }

        // Second pass of the C16 rule, exactly as the scan does it: a directory that survived its
        // own attributes still goes if an ancestor inside this same delta did not.
        directories.RemoveAll(d => perimeter.IsExcluded(d.Path));

        foreach (var item in placed.Items.Where(i => !i.Entry.IsDirectory))
        {
            var root = perimeter.GoverningRoot(item.Path);
            var extension = FileFilter.GetExtension(item.Entry.Name);

            var insidePerimeter = root is not null
                && FileFilter.IsInsidePerimeter(item.Path, item.Entry.Attributes, filters[root])
                && !perimeter.IsExcluded(item.Path);
            var allowedType = root is not null && FileFilter.IsAllowedType(extension, filters[root]);

            if (insidePerimeter && allowedType)
            {
                indexable.Add(item with
                {
                    Extension = extension,
                    Category = FileFilter.ResolveCategory(extension, categoryMap),
                });
            }
            else if (item.Existing is not null)
            {
                // Only rows that exist have a verdict to correct; a file the catalog never held
                // and does not want now is simply not our business.
                outside.Add(item);
            }
        }

        return (directories, indexable, outside);
    }

    // ── writes ────────────────────────────────────────────────────────────────

    private async Task<int> MergeFilesAsync(
        int volumeId,
        string mountRoot,
        List<PlacedItem> indexable,
        IReadOnlyDictionary<string, int> directoryIdByPath,
        CancellationToken ct)
    {
        if (indexable.Count == 0)
        {
            return 0;
        }

        // The journal carries no size and no file timestamps, so they are read from disk — for the
        // handful of files a delta names, which is the whole difference from the full snapshot,
        // where the same read costs one syscall per file on the volume.
        var metadata = await _metadataReader.ReadAsync(
            mountRoot, indexable.Select(i => i.Path).ToList(), ct);

        var merged = 0;
        foreach (var chunk in indexable.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var indexedUtc = DateTime.UtcNow;
            var entities = new List<FileEntry>(chunk.Length);
            foreach (var item in chunk)
            {
                if (!metadata.TryGetValue(item.Path, out var meta))
                {
                    // Gone between the journal read and now. Its own delete record is already in
                    // the journal and the next pass will act on it.
                    continue;
                }

                if (ResolveDirectoryId(item, directoryIdByPath) is not { } directoryId)
                {
                    // Unreachable with a coherent catalog: the file's parent was resolved through a
                    // directory row or through a directory this same delta just inserted. Loud
                    // rather than silent (§9) — the consequence would be a file quietly missing
                    // from the index until the next full scan.
                    _logger.LogWarning(
                        "Volume {VolumeId}: '{Path}' has no directory row for '{Parent}'; skipped by this delta.",
                        volumeId, item.Path, item.ParentPath);
                    continue;
                }

                entities.Add(new FileEntry
                {
                    VolumeId = volumeId,
                    DirectoryId = directoryId,
                    Name = item.Entry.Name,
                    Extension = item.Extension,
                    Category = item.Category,
                    SizeBytes = meta.SizeBytes,
                    FileCreatedUtc = meta.CreatedUtc,
                    FileModifiedUtc = meta.ModifiedUtc,
                    Attributes = item.Entry.Attributes,
                    UsnFileRef = unchecked((long)item.Entry.FileReferenceNumber),
                    IsIncluded = true,
                    IsPresent = true,
                    LastIndexedUtc = indexedUtc,
                });
            }

            if (entities.Count == 0)
            {
                continue;
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await _bulkWriter.MergeScannedFilesAsync(volumeId, entities, indexedUtc, ct);
            await _ftsIndex.SyncFilesAsync(result.AffectedFileIds, ct);
            await tx.CommitAsync(CancellationToken.None);

            merged += result.Inserted + result.Updated;
        }

        return merged;
    }

    /// <summary>
    /// Writes the two soft verdicts §6 keeps apart, and never confuses them.
    ///
    /// <para><b>Absent</b> — the journal said the object was deleted, or said it is no longer at
    /// the location the row records (renamed away, moved away). Both are evidence about the disk.
    /// The guard is <c>IsIncluded = 1</c>, which is the absence pass's own predicate: a row the
    /// user's perimeter already excludes is not something any scan looks for, so no scan would
    /// have flagged it either.</para>
    ///
    /// <para><b>Excluded</b> — the object is still exactly where the row says, but the perimeter
    /// no longer covers it. That is a filter decision, reversible without a re-scan (§4), so the
    /// cause is recorded and <c>IsPresent</c> is left strictly alone (step 11g/11h). It reaches a
    /// row two ways, and both are needed: the loop below, for the rows this delta actually names,
    /// and <see cref="ExcludeSubtreesAsync"/>, for the rows underneath a folder that left the
    /// perimeter and that no record names at all.</para>
    /// </summary>
    private async Task<(int Absent, int Excluded)> ReconcileAsync(
        int volumeId,
        List<ulong> deletedFrns,
        List<PlacedItem> outside,
        IReadOnlyDictionary<string, int> directoryIdByPath,
        ScanPerimeter perimeter,
        CancellationToken ct)
    {
        var goneFileIds = new List<int>();
        var goneDirectoryIds = new List<int>();

        // One bucket per cause, because each is undone by a different owner (11h, and step 16 for
        // the path half): writing the wrong flag would either pin a row out for ever or let it back
        // in on a setting that says nothing about it.
        var excludedByCause = new Dictionary<ScanSkipCause, List<int>>();

        if (deletedFrns.Count > 0)
        {
            var deletedFiles = await LoadFilesByFrnAsync(volumeId, deletedFrns, ct);
            goneFileIds.AddRange(deletedFiles.Values.Where(f => f.IsIncluded).Select(f => f.Id));

            // Directories are addressed by the path their own row records, which is the only place
            // a deleted folder still exists — and the perimeter is asked about it for the same
            // reason DirectoryMerger asks: a folder the user has told us not to look at is not one
            // any scan would report as gone, so the delta must not either.
            var deletedDirectories = await LoadDirectoriesByFrnAsync(volumeId, deletedFrns, ct);
            goneDirectoryIds.AddRange(deletedDirectories.Values
                .Where(d => d.IsPresent && perimeter.Covers(d.Path))
                .Select(d => d.Id));
        }

        foreach (var item in outside)
        {
            var existing = item.Existing!;
            if (!existing.IsIncluded)
            {
                continue; // already outside; nothing this pass can add
            }

            var stillWhereTheRowSaysItIs =
                ResolveDirectoryId(item, directoryIdByPath) == existing.DirectoryId
                && string.Equals(existing.Name, item.Entry.Name, StringComparison.OrdinalIgnoreCase);

            if (!stillWhereTheRowSaysItIs)
            {
                goneFileIds.Add(existing.Id);
                continue;
            }

            // Every cause that applies, not the first one: they sum, and writing only one would let
            // the row back in the moment that one is undone (step 16). A row landing in two buckets
            // is counted twice in the `excluded` tally below — a number that goes to the log, not to
            // a decision.
            foreach (var cause in perimeter.SkipVerdict(item.Path))
            {
                if (!excludedByCause.TryGetValue(cause, out var bucket))
                {
                    excludedByCause[cause] = bucket = [];
                }

                bucket.Add(existing.Id);
            }

            // Step 18 closed the first two of the three shapes below (the third is still open):
            // Directories now carry ExcludedByScan, EFFECTIVE (the folder or an ancestor), written
            // by the scan's closing pass and by ExcludeSubtreesAsync, cleared only by a walk that
            // goes into the folder. PlaceAsync reads it off every catalog parent this delta
            // resolves against and Classify seeds the perimeter with it (inherited), so a file
            // written inside a hidden folder in a later tick is outside, and a folder created
            // there never enters. The text is kept as it was written, because it is the record of
            // WHY the column exists.
            //
            // KNOWN HOLE, stated rather than implied by silence: this loop can find no cause at
            // all. The perimeter only records DIRECTORIES it excluded, so a file whose OWN
            // attributes turned against it — one that just became Hidden — is refused indexing
            // above and then answers "inside" here, and the row keeps IsIncluded = 1 until a full
            // scan passes. The verdict is not wrong, it is missing: nothing recorded the file
            // itself. Considered for the A3 round and left: closing it means recording every
            // FILE's perimeter verdict at classify time, which is a change to what Classify
            // produces and a defect of its own — this pass is about the folder that leaves the
            // perimeter, and the rows underneath it that no record names.
            //
            // THE SAME BOUNDARY FROM THE OTHER SIDE, and it is the more useful way round, because
            // it UNDOES what ExcludeSubtreesAsync just wrote. `perimeter` knows only the subtrees
            // THIS delta excluded. So a file inside a hidden folder, in a later tick that does not
            // name the folder, is judged on its own clean attributes and its own clean path,
            // passes the `insidePerimeter` test above, lands in `indexable`, and the merge writes
            // it back IsIncluded = 1 with all four causes cleared — because the merge has, quite
            // correctly, just seen the file on disk. Measured, not assumed (throwaway probe, two
            // ticks on one hidden folder):
            //
            //     tick 1 (the folder turns Hidden)  -> inc=False scan=True  path=False
            //     tick 2 (only the FILE is written) -> inc=True  scan=False path=False
            //
            // It loses the ATTRIBUTE half only. The path half survives the same sequence, because
            // FileFilter.IsPathExcluded reads the FILE's own relative path and the excluded
            // segment is in it — the same probe gives inc=False, path=True after both ticks. That
            // asymmetry is the one PerimeterVerdict is built around: a fact of the settings can be
            // re-derived from the catalog, a fact of the disk cannot.
            //
            // Not a regression of the subtree pass: a row a FULL SCAN excluded for attributes has
            // always come back this way, so the installed service has it too. What changed is that
            // it now MATTERS — before, the delta never excluded those rows, so there was nothing
            // for ordinary write traffic to undo. The catalog can now hold a state neither road
            // produces on its own: the subtree excluded, and the rows that happened to be written
            // since back inside it.
            //
            // The hole has a second half, and it is not "fails to remove" but "adds". A NEW
            // subdirectory created inside that hidden folder has no catalogued row to re-admit:
            // Classify judges it on its OWN clean attributes, EvaluatePerimeter answers IsInside, it
            // lands in `directories`, DirectoryMerger inserts the row, and files created under it
            // are indexed. The C16 second pass does not stop it either — `perimeter` only knows the
            // subtrees THIS delta excluded, and this delta does not name the hidden folder. So the
            // catalog does not merely let rows back inside the excluded subtree: it GROWS inside it.
            //
            // And a third shape, which is neither "undoes" nor "adds" but "never looks": the SAME
            // TICK. A folder X moved INTO the folder this delta just excluded is judged by Classify
            // on its OWN clean attributes, lands in `directories`, and is then dropped by the C16
            // second pass (`directories.RemoveAll`, above) because it sits under a subtree this
            // delta excluded. From there nobody names it again — it is not in `outside`, which
            // collects files only, nor in `goneDirectoryIds`, which collects deleted FRNs — so its
            // row keeps the OLD MaterializedPath with IsPresent = 1, and the files under it keep
            // IsIncluded = 1. ExcludeSubtreesAsync cannot reach them either: InSubtree matches by
            // PATH, and the row's path is still the old one, outside the excluded folder. A full
            // scan of the same world marks that row absent and the files with it, so the two roads
            // diverge. Pre-existing (the RemoveAll is 14d's), and repaired by the next full scan.
            //
            // The three are one family — the delta's perimeter is a fact about THIS tick, and rows
            // are addressed by the path they currently record — but they do not close the same way.
            // The first two need a fact the catalog does not have: no row says "that folder is
            // hidden", because Directories carry no inclusion flag (the product decision of 11g),
            // and the alternative is a disk read per file per delta. The third is different, and
            // worth naming as such: here the delta HOLDS the knowledge — `perimeter` knows the
            // excluded subtree — and throws it away, because the item is discarded by path at the
            // moment it would have to be carried on by identity. Closing that one is a change to
            // what Classify hands on, not to what the catalog stores. All three outside this step;
            // the full scan repairs all three, because it asks the perimeter about every directory
            // it walks.
        }

        // FIRST, and the ordering is the scan's own trick (see
        // BulkIndexWriter.ReconcileUnseenFilesAsync): the rows this flags IsIncluded = 0 drop out
        // of the absence pass by themselves, because that pass's SQL guard is IsIncluded = 1. So a
        // file deleted from a folder this same delta excluded ends up EXCLUDED and not ABSENT —
        // which is exactly what a full scan of the same world produces, since it never looks inside
        // that folder and its absence pass skips what its exclusion pass has just flagged.
        var excluded = await ExcludeSubtreesAsync(volumeId, perimeter, ct);

        var absent = await MarkFilesAbsentAsync(goneFileIds, ct);
        absent += await MarkDirectoriesAbsentAsync(goneDirectoryIds, ct);

        return (absent, excluded + await ExcludeFilesAsync(excludedByCause, ct));
    }

    /// <summary>
    /// Carries a subtree exclusion this delta decided to the rows the catalog ALREADY holds under
    /// it — the hole A3 names, and a hole only the delta has.
    ///
    /// <para>A full scan does not need this: it asks the perimeter about EVERY directory of the
    /// catalog when it closes, so each descendant of a folder that just turned Hidden produces its
    /// own skipped area. A delta cannot — it sees what CHANGED, and the rows under that folder did
    /// not change, so no journal record names them. Without this pass they keep
    /// <c>IsIncluded = 1</c> until the next full scan: navigable in the Catalog and findable in
    /// Search, inside a folder the user's perimeter excludes. An exclusion silently not applied is
    /// the worst shape of failure here, because the user believes they decided something.</para>
    ///
    /// <para><b>Set-based, and addressed by DIRECTORY on both halves.</b> One SELECT of the
    /// subtree's directory ids through <see cref="DirectoryQueries.InSubtree"/> (K5 — the single
    /// spelling of that predicate), one UPDATE per cause per chunk of them, and the index pruned
    /// through <see cref="IFileSearchIndex.SyncDirectoriesAsync"/> rather than the per-file
    /// <c>RemoveAsync</c> loop the rest of this class uses. That loop is the right shape for the
    /// handful of files a delta names by hand; a subtree can hold thousands and none of them is
    /// named anywhere. Both halves are handed the SAME chunk of ids, so the flags and the index
    /// cannot be written for two different sets of directories.</para>
    ///
    /// <para><b>The number of STATEMENTS is per excluded DIRECTORY of this delta, not per file
    /// behind them</b> — which is the claim worth making, and it is not the same as "the rows
    /// underneath are free". They are not: the UPDATE and the index pair each touch every one of
    /// them, and that work is the point of the pass. What the shape buys is that the work stays
    /// inside SQLite instead of arriving as one round trip per file. What each excluded directory
    /// pays is <b>not</b> a seek on <c>IX_Directories_MaterializedPath</c>: measured on the real
    /// catalog (2026-09-03, 113 831 directories on the system volume) the plan is
    /// <c>SEARCH Directories USING INDEX IX_Directories_VolumeId_ParentId (VolumeId=?)</c> — SQLite
    /// cannot drive a prefix index from a <c>LIKE</c> whose pattern is a parameter, so the volume's
    /// directory rows are walked and the prefix is tested per row. **31 ms** per excluded directory
    /// there, so a tick naming many of them is seconds inside the worker; a volume with few
    /// directories is nothing. Then its updates, and on a system volume each excluded directory can
    /// stand over thousands of rows. A directory the catalog never held returns an empty id list and
    /// stops there, which is the common case for journal traffic in places this index has never
    /// been.</para>
    ///
    /// <para><b>A chunk is the unit of atomicity as well as of statement size</b>, exactly like the
    /// two absence passes below and for the reason <see cref="BatchSize"/> is documented with: the
    /// flags and the index prune for the same directories land together or not at all — an index
    /// still listing a row the perimeter now excludes is a search result the user can see — while
    /// the write lock is held for at most <see cref="BatchSize"/> directories at a time. One
    /// transaction around the whole pass would have been bounded by nothing: the number of subtrees
    /// is the number of directory records this delta carries, and after a long shutdown that is not
    /// a handful. Committing per chunk costs nothing in safety, because the cursor is written LAST
    /// and every statement here is idempotent.</para>
    ///
    /// <para><b>The index is only touched when a row actually moved.</b> Without the guard, a
    /// subtree whose rows are all already excluded still pays a DELETE plus an INSERT over the whole
    /// of it, to produce the state that was already there. What makes that recur is <b>not</b>
    /// ordinary write traffic inside the folder: NTFS journals the change to the FILE, so writing a
    /// file does not emit a record for its directory, and <see cref="Coalesce"/> plus the cursor
    /// keep a record already consumed from coming back. What does recur reaches DOWNWARD in every
    /// case, because <see cref="DirectoryQueries.InSubtree"/> matches the rows at or below an
    /// entry's own path and never anything above it: the folder turning up again when IT changes
    /// (another attribute write, a rename, a security change) and being re-decided over a subtree
    /// that is already out; nested or duplicate entries within one tick, where an excluded
    /// directory under another excluded directory covers the same rows a second time; and replay,
    /// a delta re-offered after a crash reaching all of them again. The guard is sound because of
    /// the columns this pass writes. It writes three — the cause's own flag, <c>IsIncluded</c>, and
    /// the row's audit stamp — and of those three <c>FileSearchIndex.IndexableSql</c> reads exactly
    /// one, <c>IsIncluded</c>. So a pass that updated no row cannot have moved anything the index's
    /// membership is a function of, and nothing it could have made stale is stale — which is a
    /// statement about THIS pass, not a promise that nobody else makes the index stale (see the
    /// KNOWN HOLE in <see cref="ReconcileAsync"/>).</para>
    ///
    /// <para><b>Not</b> what an earlier version of this note claimed, and worth leaving written
    /// down because it is the plausible-sounding one: a NEW subdirectory created inside the
    /// excluded folder re-stamps nothing. Excluded on its own account, it is asked about ITS path,
    /// and a directory the catalog has never seen has no rows at or below it — the empty id list
    /// takes the <c>continue</c> below, before the guard is ever reached. Inside the perimeter on
    /// its own account, it produces no entry here at all: <c>Classify</c> drops it from
    /// <c>directories</c> through the C16 second pass without ever recording it as an excluded
    /// root. Either way nothing ABOVE it is touched, because there is no upward direction in this
    /// pass.</para>
    ///
    /// <para><b>Each entry carries its OWN causes, not the union with its ancestors'</b>, and that
    /// is complete: an excluded ancestor is itself an entry in this set, and its own pass covers
    /// the very same subtree — so a row under two excluded folders is reached twice and ends up
    /// with both causes, which is what "the causes sum" means here. The returned tally counts such
    /// a row once per (subtree, cause) that had something to write to it, the same double count
    /// <see cref="ExcludeFilesAsync"/>'s callers already live with: it is a number for the log, not
    /// for a decision.</para>
    ///
    /// <para>Only <c>Files</c>. <c>Directories</c> have no inclusion flag and a folder that exists
    /// on disk exists (11g), and <c>IsPresent</c> is never touched: an exclusion is not an absence
    /// (§6).</para>
    /// </summary>
    private async Task<int> ExcludeSubtreesAsync(int volumeId, ScanPerimeter perimeter, CancellationToken ct)
    {
        var roots = perimeter.ExcludedSubtreeRoots;
        if (roots.Count == 0)
        {
            return 0;
        }

        var excluded = 0;

        // One stamp for the whole pass: it is one verdict, however many transactions carry it, and
        // rows of the same subtree differing by a few microseconds would only invite someone to
        // read meaning into the spread.
        var now = DateTime.UtcNow;

        foreach (var (path, verdict) in roots)
        {
            ct.ThrowIfCancellationRequested();

            var directoryIds = await _db.Directories.AsNoTracking()
                .InSubtree(volumeId, path)
                .Select(d => d.Id)
                .ToListAsync(ct);

            if (directoryIds.Count == 0)
            {
                // An excluded folder the catalog never held — the normal case on a system volume,
                // where most of the journal's traffic happens in places this index has never been.
                continue;
            }

            foreach (var chunk in directoryIds.Chunk(BatchSize))
            {
                ct.ThrowIfCancellationRequested();

                var ids = chunk.ToList();
                var written = 0;

                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                foreach (var cause in verdict)
                {
                    written += await ExcludeForCauseAsync(
                        _db.Files.Where(f => ids.Contains(f.DirectoryId)), cause, now, ct);
                }

                // Step 18: the DIRECTORY rows remember the attribute cause too, so the next tick
                // can inherit it off the parent row. Attributes only — a path segment is
                // re-derived from an item's own path — and only rows that do not carry it yet.
                if (verdict.ExcludedByAttributes)
                {
                    await _db.Directories
                        .Where(d => ids.Contains(d.Id) && !d.ExcludedByScan)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(d => d.ExcludedByScan, true)
                            .SetProperty(d => d.UpdatedUtc, now), ct);
                }

                if (written > 0)
                {
                    await _ftsIndex.SyncDirectoriesAsync(ids, ct);
                }

                await tx.CommitAsync(CancellationToken.None);
                excluded += written;
            }
        }

        return excluded;
    }

    private async Task<int> MarkFilesAbsentAsync(List<int> fileIds, CancellationToken ct)
    {
        var marked = 0;
        foreach (var chunk in fileIds.Distinct().Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var ids = chunk.ToList();
            var now = DateTime.UtcNow;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // ExecuteUpdate bypasses SaveChanges and therefore the auditing interceptor, so the
            // row-audit stamp is written explicitly — the same rule DirectoryMerger follows.
            marked += await _db.Files
                .Where(f => ids.Contains(f.Id) && f.IsIncluded && f.IsPresent)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.IsPresent, false)
                    .SetProperty(f => f.UpdatedUtc, now), ct);

            // …and out of the search index with them, in the same transaction: a file that is no
            // longer where it was indexed must stop being a hit. Row by row rather than the
            // volume-wide prune a scan closes with — a delta names a handful of files, and that
            // prune re-reads every row of the volume.
            foreach (var id in ids)
            {
                await _ftsIndex.RemoveAsync(id, ct);
            }

            await tx.CommitAsync(CancellationToken.None);
        }

        return marked;
    }

    private async Task<int> MarkDirectoriesAbsentAsync(List<int> directoryIds, CancellationToken ct)
    {
        var marked = 0;
        foreach (var chunk in directoryIds.Distinct().Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var ids = chunk.ToList();
            var now = DateTime.UtcNow;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            marked += await _db.Directories
                .Where(d => ids.Contains(d.Id) && d.IsPresent)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.IsPresent, false)
                    .SetProperty(d => d.UpdatedUtc, now), ct);
            await tx.CommitAsync(CancellationToken.None);
        }

        return marked;
    }

    /// <summary>
    /// Writes every cause this delta proved, for every row it proved one about — in ONE
    /// transaction.
    ///
    /// <para>One transaction because the causes SUM and a row can be in two buckets: a
    /// transaction per cause meant such a row was written through two of them, and a crash in
    /// between left it carrying one cause and not the other. Nothing catastrophic follows (the
    /// cursor is written last, so the delta simply replays, and every write here is idempotent) —
    /// but it is a window this pass did not have to open, and the two flags describe ONE verdict.
    /// The chunking inside stays: it bounds the size of each statement, not of the transaction,
    /// and a delta names a handful of files.</para>
    /// </summary>
    private async Task<int> ExcludeFilesAsync(
        Dictionary<ScanSkipCause, List<int>> fileIdsByCause, CancellationToken ct)
    {
        if (fileIdsByCause.Count == 0)
        {
            return 0;
        }

        var excluded = 0;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        foreach (var (cause, fileIds) in fileIdsByCause)
        {
            foreach (var chunk in fileIds.Distinct().Chunk(BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var ids = chunk.ToList();
                var now = DateTime.UtcNow;

                excluded += await ExcludeForCauseAsync(
                    _db.Files.Where(f => ids.Contains(f.Id)), cause, now, ct);

                foreach (var id in ids)
                {
                    await _ftsIndex.RemoveAsync(id, ct);
                }
            }
        }

        await tx.CommitAsync(CancellationToken.None);
        return excluded;
    }

    /// <summary>
    /// One cause, one column, one statement — the delta's spelling of
    /// <c>BulkIndexWriter.ExcludeForCauseAsync</c>, guard included, so the two roads write a row
    /// out the same way.
    ///
    /// <para>The invariant of §6: <c>IsIncluded</c> is the derived column, the cause flag is the
    /// fact. Only the cause being proved is written; the other three are left exactly as they are,
    /// because each is undone by a different owner (11h, and step 16 for the path half).</para>
    ///
    /// <para><b>The guard is the cause's OWN flag, never <c>IsIncluded</c> alone</b>: a row already
    /// excluded for a different reason still has to learn this one, or undoing that other reason
    /// lets it back in. The <c>OR IsIncluded = 1</c> half is the repair net under a broken
    /// invariant, for the same reason the writer's version carries it. Together they make a replay
    /// write nothing at all — which is the property "the cursor is written LAST" rests on, and it
    /// stopped being a formality the moment the subtree pass began touching rows that no journal
    /// record names.</para>
    /// </summary>
    private static Task<int> ExcludeForCauseAsync(
        IQueryable<FileEntry> rows, ScanSkipCause cause, DateTime now, CancellationToken ct) =>
        cause switch
        {
            ScanSkipCause.InactiveRoot => rows
                .Where(f => !f.ExcludedByRoot || f.IsIncluded)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.ExcludedByRoot, true)
                    .SetProperty(f => f.IsIncluded, false)
                    .SetProperty(f => f.UpdatedUtc, now), ct),
            ScanSkipCause.ExcludedPath => rows
                .Where(f => !f.ExcludedByPath || f.IsIncluded)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.ExcludedByPath, true)
                    .SetProperty(f => f.IsIncluded, false)
                    .SetProperty(f => f.UpdatedUtc, now), ct),
            ScanSkipCause.ExcludedAttributes => rows
                .Where(f => !f.ExcludedByScan || f.IsIncluded)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.ExcludedByScan, true)
                    .SetProperty(f => f.IsIncluded, false)
                    .SetProperty(f => f.UpdatedUtc, now), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unknown scan skip cause."),
        };

    // ── cursor ────────────────────────────────────────────────────────────────

    private async Task CheckpointAsync(Volume volume, long nextUsn, CancellationToken ct)
    {
        if (volume.LastUsn == nextUsn)
        {
            return;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        volume.LastUsn = nextUsn;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(CancellationToken.None);
    }

    /// <summary>
    /// Drops the cursor so this volume stops taking the short road until a full scan writes a new
    /// one. Without it a dead cursor would be re-detected on every tick, and every tick would ask
    /// for the same rescan and raise the same notification.
    /// </summary>
    private async Task InvalidateCursorAsync(Volume volume, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        volume.LastUsn = null;
        volume.UsnJournalId = null;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(CancellationToken.None);
    }

    // ── lookups ───────────────────────────────────────────────────────────────

    private static int? ResolveDirectoryId(PlacedItem item, IReadOnlyDictionary<string, int> idByPath) =>
        item.ParentId ?? (idByPath.TryGetValue(item.ParentPath, out var id) ? id : null);

    private async Task<Dictionary<ulong, CatalogDirectory>> LoadDirectoriesByFrnAsync(
        int volumeId, IEnumerable<ulong> frns, CancellationToken ct)
    {
        var map = new Dictionary<ulong, CatalogDirectory>();
        foreach (var chunk in frns.Distinct().Chunk(BatchSize))
        {
            var signed = chunk.Select(f => unchecked((long)f)).ToList();
            var rows = await _db.Directories.AsNoTracking()
                .Where(d => d.VolumeId == volumeId && d.UsnFileRef != null && signed.Contains(d.UsnFileRef.Value))
                .Select(d => new { d.Id, d.UsnFileRef, d.MaterializedPath, d.IsPresent, d.ExcludedByScan })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                map[unchecked((ulong)row.UsnFileRef!.Value)] =
                    new CatalogDirectory(row.Id, row.MaterializedPath, row.IsPresent, row.ExcludedByScan);
            }
        }

        return map;
    }

    private async Task<Dictionary<ulong, CatalogFile>> LoadFilesByFrnAsync(
        int volumeId, IEnumerable<ulong> frns, CancellationToken ct)
    {
        var map = new Dictionary<ulong, CatalogFile>();
        foreach (var chunk in frns.Distinct().Chunk(BatchSize))
        {
            var signed = chunk.Select(f => unchecked((long)f)).ToList();
            var rows = await _db.Files.AsNoTracking()
                .Where(f => f.VolumeId == volumeId && f.UsnFileRef != null && signed.Contains(f.UsnFileRef.Value))
                .Select(f => new { f.Id, f.UsnFileRef, f.DirectoryId, f.Name, f.IsIncluded, f.IsPresent })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                map[unchecked((ulong)row.UsnFileRef!.Value)] =
                    new CatalogFile(row.Id, row.DirectoryId, row.Name, row.IsIncluded, row.IsPresent);
            }
        }

        return map;
    }

    // ── shapes ────────────────────────────────────────────────────────────────

    private sealed record CatalogDirectory(int Id, string Path, bool IsPresent, bool ExcludedByScan);

    private sealed record CatalogFile(int Id, int DirectoryId, string Name, bool IsIncluded, bool IsPresent);

    /// <param name="ParentId">
    /// Identity of the parent directory when the catalog already holds it. Null when the parent is
    /// a directory this same delta introduces — its identity only exists after the ensure step, so
    /// it is looked up by path then.
    /// </param>
    private sealed record PlacedItem(
        UsnEntry Entry,
        string Path,
        string ParentPath,
        int? ParentId,
        CatalogFile? Existing,
        string Extension = "",
        FileCategory Category = FileCategory.Other);

    /// <param name="InheritedExclusions">
    /// Step 18: the paths of the catalog parents this delta resolved against whose row says the
    /// folder (or an ancestor) is hidden. <see cref="Classify"/> seeds the perimeter with them, so
    /// a record inside such a folder is judged the way the scan would judge it — outside — even
    /// though its own attributes and its own path say nothing.
    /// </param>
    private sealed record PlacedDelta(List<PlacedItem> Items, int Unresolved, List<string> InheritedExclusions);
}
