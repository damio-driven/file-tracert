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

        return new PlacedDelta(items, unresolved);
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
    /// cause is recorded and <c>IsPresent</c> is left strictly alone (step 11g/11h).</para>
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

            // KNOWN HOLE, stated rather than implied by silence: this loop can find no cause at
            // all. The perimeter only records DIRECTORIES it excluded, so a file whose OWN
            // attributes turned against it — one that just became Hidden — is refused indexing
            // above and then answers "inside" here, and the row keeps IsIncluded = 1 until a full
            // scan passes. The verdict is not wrong, it is missing: nothing recorded the file
            // itself. Closing it means recording the file's own perimeter verdict at classify
            // time; it belongs with the subtree pass of A3, not here.
        }

        var absent = await MarkFilesAbsentAsync(goneFileIds, ct);
        absent += await MarkDirectoriesAbsentAsync(goneDirectoryIds, ct);

        return (absent, await ExcludeFilesAsync(excludedByCause, ct));
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

                // The invariant of §6: IsIncluded is the derived column, the cause flag is the fact.
                // Only the causes this pass can prove are written — the others are left as they
                // are, because each is undone by a different owner (step 11h, step 16).
                var rows = _db.Files.Where(f => ids.Contains(f.Id));
                excluded += cause switch
                {
                    ScanSkipCause.InactiveRoot => await rows.ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.ExcludedByRoot, true)
                        .SetProperty(f => f.IsIncluded, false)
                        .SetProperty(f => f.UpdatedUtc, now), ct),
                    ScanSkipCause.ExcludedPath => await rows.ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.ExcludedByPath, true)
                        .SetProperty(f => f.IsIncluded, false)
                        .SetProperty(f => f.UpdatedUtc, now), ct),
                    ScanSkipCause.ExcludedAttributes => await rows.ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.ExcludedByScan, true)
                        .SetProperty(f => f.IsIncluded, false)
                        .SetProperty(f => f.UpdatedUtc, now), ct),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(fileIdsByCause), cause, "Unknown scan skip cause."),
                };

                foreach (var id in ids)
                {
                    await _ftsIndex.RemoveAsync(id, ct);
                }
            }
        }

        await tx.CommitAsync(CancellationToken.None);
        return excluded;
    }

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
                .Select(d => new { d.Id, d.UsnFileRef, d.MaterializedPath, d.IsPresent })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                map[unchecked((ulong)row.UsnFileRef!.Value)] =
                    new CatalogDirectory(row.Id, row.MaterializedPath, row.IsPresent);
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

    private sealed record CatalogDirectory(int Id, string Path, bool IsPresent);

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

    private sealed record PlacedDelta(List<PlacedItem> Items, int Unresolved);
}
