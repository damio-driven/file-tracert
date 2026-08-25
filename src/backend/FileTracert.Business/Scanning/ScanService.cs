using System.ComponentModel;
using FileTracert.Business.Filtering;
using FileTracert.Business.Volumes;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Scanning;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Scanning;

/// <summary>
/// Orchestrates a <em>full</em> scan of one volume: pick the engine, gather
/// entries from Platform, apply filters, fill missing sizes, build the directory
/// tree, and bulk-write the index — all transactional and idempotent (re-scan
/// replaces the volume's index). Business never reads the disk itself; it goes
/// through the Platform ports.
/// </summary>
public sealed class ScanService
{
    private readonly FileTracertDbContext _db;
    private readonly IVolumeProbe _probe;
    private readonly IUsnReader _usnReader;
    private readonly IDirectoryEnumerator _enumerator;
    private readonly IFileMetadataReader _metadataReader;
    private readonly IBulkIndexWriter _bulkWriter;
    private readonly DirectoryMerger _directoryMerger;
    private readonly IFileSearchIndex _ftsIndex;
    private readonly INotificationPublisher _notifications;
    private readonly IScanStatusTracker _statusTracker;
    private readonly ILogger<ScanService> _logger;

    /// <summary>Push a running count to the tracker every this many enumerated items.</summary>
    private const int SeenReportInterval = 5_000;

    /// <summary>
    /// How many files are merged (and committed) per transaction. Big enough that the
    /// per-batch overhead disappears, small enough that another writer never waits more
    /// than a moment for SQLite's single write lock. Settable so tests can drive the
    /// multi-batch paths with a handful of files.
    /// </summary>
    public int FileBatchSize { get; init; } = 5_000;

    public ScanService(
        FileTracertDbContext db,
        IVolumeProbe probe,
        IUsnReader usnReader,
        IDirectoryEnumerator enumerator,
        IFileMetadataReader metadataReader,
        IBulkIndexWriter bulkWriter,
        DirectoryMerger directoryMerger,
        IFileSearchIndex ftsIndex,
        INotificationPublisher notifications,
        IScanStatusTracker statusTracker,
        ILogger<ScanService> logger)
    {
        _db = db;
        _probe = probe;
        _usnReader = usnReader;
        _enumerator = enumerator;
        _metadataReader = metadataReader;
        _bulkWriter = bulkWriter;
        _directoryMerger = directoryMerger;
        _ftsIndex = ftsIndex;
        _notifications = notifications;
        _statusTracker = statusTracker;
        _logger = logger;
    }

    public async Task ScanVolumeAsync(int volumeId, CancellationToken ct)
    {
        var volume = await _db.Volumes.FirstOrDefaultAsync(v => v.Id == volumeId, ct)
            ?? throw new InvalidOperationException($"Volume {volumeId} not found.");

        var probed = _probe.TryGetByGuid(volume.VolumeGuid)
            ?? throw new InvalidOperationException($"Volume {volume.VolumeGuid} is offline.");

        var mountRoot = probed.MountPoints.FirstOrDefault()
            ?? throw new InvalidOperationException($"Volume {volume.VolumeGuid} has no mount point.");

        var roots = await _db.WatchedRoots
            .Where(r => r.VolumeId == volumeId && r.IsActive)
            .ToListAsync(ct);
        if (roots.Count == 0)
        {
            _logger.LogInformation("Volume {VolumeId} has no active watched roots; nothing to scan.", volumeId);
            return;
        }

        // Track progress for the duration of the scan. On any failure the tracker is
        // marked failed (so the UI/poll stops showing it as running) and the error
        // still propagates to the worker for logging + the user-facing notification.
        _statusTracker.Begin(volume.Id, volume.Label);
        try
        {
            await RunScanAsync(volume, probed, mountRoot, roots, ct);
            _statusTracker.Complete(volume.Id);
        }
        catch
        {
            _statusTracker.Fail(volume.Id);
            throw;
        }
    }

    private async Task RunScanAsync(
        Volume volume, ProbedVolume probed, string mountRoot, List<WatchedRoot> roots, CancellationToken ct)
    {
        var volumeId = volume.Id;

        // Generation marker taken BEFORE anything is read: every row the merge touches gets
        // a later LastIndexedUtc, so what is still older at the end is exactly what this scan
        // did not find on disk.
        var scanStartedUtc = DateTime.UtcNow;

        var settings = await _db.AppSettings.FirstOrDefaultAsync(ct);
        var categoryMap = await _db.ExtensionCategories.ToDictionaryAsync(e => e.Extension, e => e.Category, ct);

        // For NTFS, ensure the journal exists then checkpoint its position BEFORE
        // reading the snapshot, so the future incremental catches everything that
        // changed during the scan. A volume can be NTFS yet have no active journal:
        // EnsureJournal creates it (needs admin). Whether USN is even attempted is
        // decided from the volume's actual filesystem (VolumeMapper.EngineFor), not
        // from the persisted ScanEngine — that field only records what the LAST scan
        // used, and if it were the gate, one transient failure (e.g. not elevated at
        // the time) would downgrade the volume to slow enumeration forever, with no
        // way back even after the real problem (elevation, journal state) is fixed.
        // Retrying every scan means the fast path recovers on its own next attempt.
        UsnJournalState? checkpoint = null;
        if (VolumeMapper.EngineFor(volume.FileSystem) == VolumeScanEngine.UsnJournal)
        {
            try
            {
                _usnReader.EnsureJournal(volume.VolumeGuid);

                // Both halves of the cursor, taken together and from the same query: the position
                // AND the journal instance it belongs to. A position without its id cannot be
                // told apart from a position into a journal that has since been recreated.
                checkpoint = _usnReader.GetJournalState(volume.VolumeGuid);
                volume.ScanEngine = VolumeScanEngine.UsnJournal;
            }
            catch (Win32Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "USN journal unavailable for volume {VolumeId} ({Guid}); falling back to enumeration for this scan.",
                    volumeId, volume.VolumeGuid);
                volume.ScanEngine = VolumeScanEngine.Enumeration;

                // Resilience, not silence: the scan continues via enumeration, but the
                // user should know their NTFS volume isn't using the fast incremental path.
                await _notifications.PublishAsync(
                    NotificationSeverity.Warning,
                    "Scan",
                    $"USN journal non disponibile per «{volume.Label ?? volume.VolumeGuid}»",
                    $"Indicizzazione tramite enumerazione (più lenta). Dettaglio: {ex.Message} (codice {ex.NativeErrorCode}). " +
                    "Eseguire il servizio come amministratore abilita il giornale USN. Verrà ritentato al prossimo scan.",
                    volume.Id,
                    ct);
            }
        }
        else
        {
            volume.ScanEngine = VolumeScanEngine.Enumeration;
        }

        var filters = await ResolveRootFiltersAsync(volume, roots, settings, ct);
        var (dirItems, fileItems, perimeter) = GatherAndFilter(volume, mountRoot, roots, filters, ct);

        _statusTracker.SetPhase(volumeId, ScanPhase.ReadingMetadata);
        var resolvedFiles = await ResolveFilesAsync(volume, mountRoot, fileItems, categoryMap, ct);

        _statusTracker.SetPhase(volumeId, ScanPhase.Writing);
        await PersistAsync(volume, dirItems, resolvedFiles, perimeter, checkpoint, scanStartedUtc, ct);

        _logger.LogInformation(
            "Scanned volume {VolumeId}: {Dirs} directories, {Files} files.",
            volumeId, dirItems.Count, resolvedFiles.Count);
    }

    /// <summary>
    /// Resolves the effective filter once per watched root, through the rule the incremental path
    /// shares (<see cref="RootFilters"/>). A malformed override JSON is not silently ignored: it is
    /// logged, raised as a user-visible notification, and the root falls back to the default filter
    /// so the scan still proceeds.
    /// </summary>
    private Task<Dictionary<string, EffectiveFilter>> ResolveRootFiltersAsync(
        Volume volume,
        List<WatchedRoot> roots,
        AppSettings? settings,
        CancellationToken ct) =>
        RootFilters.ResolveAsync(settings, roots, async (root, ex) =>
        {
            _logger.LogWarning(
                ex,
                "Invalid filter override for root '{Root}' on volume {VolumeId}; using the default filter.",
                root.RelativePath, volume.Id);

            await _notifications.PublishAsync(
                NotificationSeverity.Warning,
                "Scan",
                $"Filtro non valido per «{root.RelativePath}» su «{volume.Label ?? volume.VolumeGuid}»",
                $"L'override del filtro è malformato; uso il filtro predefinito. Dettaglio: {ex.Message}",
                volume.Id,
                ct);
        }, ct);

    private (List<ScanItem> Dirs, List<ScanItem> Files, ScanPerimeter Perimeter) GatherAndFilter(
        Volume volume,
        string mountRoot,
        List<WatchedRoot> roots,
        IReadOnlyDictionary<string, EffectiveFilter> filters,
        CancellationToken ct)
    {
        // Where this scan looks, built as it goes and handed to the write side: what falls outside
        // it is excluded (§4), not absent (§6). The roots are ordered ONCE, inside the perimeter —
        // which root governs an item is asked for every single enumerated entry (millions on a
        // real volume), but the ordering that makes "most specific" mean anything belongs to the
        // root set, not to the item (E7).
        //
        // C16: every directory the filter rejects takes its whole subtree with it. Collected
        // while streaming and applied at the end rather than as we go, because only ONE of the
        // two engines walks a tree: the USN snapshot is an MFT dump whose records arrive in no
        // particular order, so the parent may well be seen after its children. One rule, both
        // engines, no assumption about ordering.
        //
        // Boundary, deliberate: only directories INSIDE an active watched root can exclude a
        // subtree — an item outside every root is skipped below before it can be recorded. A
        // hidden folder that CONTAINS a watched root therefore does not hide it: the user pointed
        // at that subtree explicitly, and an attribute on an ancestor they never named should not
        // silently overrule them.
        var perimeter = new ScanPerimeter(filters.Keys);

        var dirs = new List<ScanItem>();
        var files = new List<ScanItem>();

        // Enumeration is the long "blind" phase: report a running count periodically so
        // the UI doesn't look frozen while the FRN map / directory walk is built.
        var seen = 0L;

        foreach (var item in EnumerateRaw(volume, mountRoot, roots, ct))
        {
            // C18: this is the long blind phase (minutes on a big volume) and the only place
            // that runs between two awaits, so it is where a shutdown has to be observed. The
            // ports honour the token too, but the consumer checks as well: a port that ignores
            // it must not be able to hold the service past its ShutdownTimeout. Nothing is
            // committed here — the scan just stops, and §9a leaves the checkpoint unwritten.
            ct.ThrowIfCancellationRequested();

            if (++seen % SeenReportInterval == 0)
            {
                _statusTracker.ReportSeen(volume.Id, seen);
            }

            // Find the most specific active root that contains this item — the same rule the
            // single-path resolution uses, spelled once (C19).
            var rootKey = perimeter.GoverningRoot(item.RelativePath);
            if (rootKey is null)
            {
                continue;
            }

            var filter = filters[rootKey];

            if (item.IsDirectory)
            {
                if (FileFilter.ShouldIncludeDirectory(item.RelativePath, item.Attributes, filter))
                {
                    dirs.Add(item);
                }
                else
                {
                    perimeter.ExcludeSubtree(item.RelativePath);
                }
            }
            else
            {
                // Both halves of the filter asked once, and kept apart: which one rejected the file
                // decides whether the closing pass has anything to say about it. Asking through
                // ShouldIncludeFile and then re-asking the perimeter on the reject branch spent a
                // second path split on every rejected item of a volume (E7 territory).
                var extension = FileFilter.GetExtension(item.Name);
                var insidePerimeter = FileFilter.IsInsidePerimeter(item.RelativePath, item.Attributes, filter);
                var allowedType = FileFilter.IsAllowedType(extension, filter);

                if (insidePerimeter && allowedType)
                {
                    files.Add(item);
                }
                else if (!insidePerimeter && allowedType)
                {
                    // On disk, outside the perimeter: the closing pass must call it excluded, not
                    // absent. Only if its TYPE is allowed, though — a file the allow-list rejects
                    // cannot have a row waiting for that verdict (a row exists only because the
                    // file passed the allow-list when it was indexed, and a narrowing of the
                    // allow-list is FilterReconciler's job, not the scan's). Without that guard
                    // every desktop.ini and Thumbs.db of a watched volume would be carried through
                    // the merge to say nothing.
                    perimeter.SkipFile(item.RelativePath);
                }
            }
        }

        _statusTracker.ReportSeen(volume.Id, seen);
        DropExcludedSubtrees(volume, dirs, files, perimeter);
        return (dirs, files, perimeter);
    }

    /// <summary>
    /// Second pass of the C16 rule: drop what survived its own attributes but lives under a
    /// folder that did not. Dropping the descendant FILES is what also stops the ancestor walk
    /// in <see cref="BuildDirectoryTree"/> from resurrecting the excluded folder as a
    /// materialized row — a file the scan never keeps cannot create its own ancestors.
    /// </summary>
    private void DropExcludedSubtrees(
        Volume volume, List<ScanItem> dirs, List<ScanItem> files, ScanPerimeter perimeter)
    {
        if (perimeter.ExcludedSubtreeCount == 0)
        {
            return;
        }

        var droppedDirs = dirs.RemoveAll(d => perimeter.IsExcluded(d.RelativePath));
        var droppedFiles = files.RemoveAll(f => perimeter.IsExcluded(f.RelativePath));
        perimeter.PruneSkippedFilesUnderExcludedSubtrees();

        if (droppedDirs + droppedFiles > 0)
        {
            _logger.LogInformation(
                "Volume {VolumeId}: {Dirs} directory(ies) and {Files} file(s) skipped because an " +
                "ancestor folder is excluded by the filter.",
                volume.Id, droppedDirs, droppedFiles);
        }
    }

    private IEnumerable<ScanItem> EnumerateRaw(
        Volume volume, string mountRoot, List<WatchedRoot> roots, CancellationToken ct)
    {
        if (volume.ScanEngine == VolumeScanEngine.UsnJournal)
        {
            // USN enumerates the whole volume; root filtering happens upstream.
            foreach (var e in _usnReader.ReadFullSnapshot(volume.VolumeGuid, ct))
            {
                yield return new ScanItem(
                    ScanPath.Normalize(e.RelativePath),
                    e.Name,
                    e.IsDirectory,
                    SizeBytes: null,
                    CreatedUtc: null,
                    ModifiedUtc: null,
                    e.Attributes,
                    e.FileReferenceNumber);
            }

            yield break;
        }

        foreach (var root in roots)
        {
            foreach (var e in _enumerator.Enumerate(mountRoot, root.RelativePath, ct))
            {
                yield return new ScanItem(
                    ScanPath.Normalize(e.RelativePath),
                    e.Name,
                    e.IsDirectory,
                    e.SizeBytes,
                    e.CreatedUtc,
                    e.ModifiedUtc,
                    e.Attributes,
                    Frn: null);
            }
        }
    }

    private async Task<List<ResolvedFile>> ResolveFilesAsync(
        Volume volume,
        string mountRoot,
        List<ScanItem> fileItems,
        IReadOnlyDictionary<string, FileCategory> categoryMap,
        CancellationToken ct)
    {
        // USN snapshot has no size/timestamps → read them from disk, but only for
        // the files that survived the filter.
        IReadOnlyDictionary<string, FileMetadata> metadata =
            volume.ScanEngine == VolumeScanEngine.UsnJournal
                ? await _metadataReader.ReadAsync(mountRoot, fileItems.Select(f => f.RelativePath).ToList(), ct)
                : new Dictionary<string, FileMetadata>();

        var resolved = new List<ResolvedFile>(fileItems.Count);
        foreach (var item in fileItems)
        {
            long size;
            DateTime created;
            DateTime modified;

            if (item.SizeBytes is { } itemSize)
            {
                size = itemSize;
                created = item.CreatedUtc ?? default;
                modified = item.ModifiedUtc ?? default;
            }
            else if (metadata.TryGetValue(item.RelativePath, out var meta))
            {
                size = meta.SizeBytes;
                created = meta.CreatedUtc;
                modified = meta.ModifiedUtc;
            }
            else
            {
                // File vanished between snapshot and stat — skip it.
                continue;
            }

            var extension = FileFilter.GetExtension(item.Name);
            resolved.Add(new ResolvedFile(
                item,
                size,
                created,
                modified,
                extension,
                FileFilter.ResolveCategory(extension, categoryMap)));
        }

        return resolved;
    }

    /// <summary>
    /// Reconciles what the scan saw with what is already in the catalog, in short
    /// transactions.
    /// </summary>
    /// <remarks>
    /// <para><b>Merge, not replace.</b> The previous implementation truncated the volume and
    /// reinserted it. That destroyed the <c>Pending*</c> overlay (§5) and the row identities
    /// <c>OperationJobItems.FileId</c> points at, on every single re-scan.</para>
    /// <para><b>Short transactions.</b> The truncate + reinsert also held SQLite's single
    /// write lock for the entire scan — minutes on a system drive — so the sync worker, the
    /// queue and the API all took SQLITE_BUSY ("database is locked"). Each batch now commits
    /// on its own and the lock is released in between.</para>
    /// <para><b>Checkpoint last.</b> <c>LastFullScanUtc</c> / <c>LastUsn</c> are written in
    /// their own final transaction: a scan interrupted halfway must not look complete, or the
    /// incremental USN pass would resume from a position covering rows that were never
    /// written. Nothing needs repairing after a crash — the merge is idempotent, so the next
    /// scan simply converges.</para>
    /// </remarks>
    private async Task PersistAsync(
        Volume volume,
        List<ScanItem> dirItems,
        List<ResolvedFile> files,
        ScanPerimeter perimeter,
        UsnJournalState? checkpoint,
        DateTime scanStartedUtc,
        CancellationToken ct)
    {
        var scannedDirs = BuildDirectoryTree(dirItems, files);
        var merge = await _directoryMerger.MergeAsync(volume.Id, scannedDirs, perimeter, FileBatchSize, ct);

        // First scan of a volume = empty table: there is nothing to reconcile against, so the
        // batches go straight through the bulk insert (§3, "prima scansione = BulkInsert puro
        // sul caso ideale") and the search index is populated once at the end. Matching every
        // row against an empty set would be pure overhead on the largest scan of all.
        var isFirstScan = !await _db.Files.AnyAsync(f => f.VolumeId == volume.Id, ct);

        var written = 0;
        foreach (var chunk in files.Chunk(FileBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var indexedUtc = DateTime.UtcNow;
            var entities = chunk.Select(f => ToEntity(volume.Id, f, merge.IdByPath, indexedUtc)).ToList();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            if (isFirstScan)
            {
                await _bulkWriter.BulkInsertFilesAsync(entities, ct);
                written += entities.Count;
            }
            else
            {
                var result = await _bulkWriter.MergeScannedFilesAsync(volume.Id, entities, indexedUtc, ct);

                // Search entries follow the rows inside the same transaction, so the index is
                // never out of step with a committed batch — and only the batch's own rows are
                // rewritten, instead of the whole volume once per scan.
                await _ftsIndex.SyncFilesAsync(result.AffectedFileIds, ct);
                written += result.Inserted + result.Updated;
            }

            // The commit is the checkpoint: once the batch is merged, cancelling must not
            // throw the work away (§3, "checkpoint the state and stop cleanly"). Shutdown is
            // observed at the top of the next iteration instead.
            await tx.CommitAsync(CancellationToken.None);

            _statusTracker.ReportWritten(volume.Id, written);
        }

        if (isFirstScan)
        {
            // Bulk insert does not read identities back, so the index is filled from the rows
            // themselves — the volume is entirely new, which is exactly what this does well.
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _ftsIndex.SyncVolumeFromDbAsync(volume.Id, ct);
            await tx.CommitAsync(CancellationToken.None);
        }

        // What the scan did not touch this run splits in two (§4/§6): what it never looked at is
        // excluded, what it looked for and did not find is gone from disk. Flagged either way,
        // never deleted, so a queued operation still finds the row it references.
        //
        // The areas are worked out BEFORE the transaction opens: it is a pass over every directory
        // of the volume, and SQLite has one writer — no reason for the rest of the process to wait
        // behind an in-memory loop.
        var skipped = SkippedAreas(volume, perimeter, merge.IdByPath);

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            var closure = await _bulkWriter.ReconcileUnseenFilesAsync(
                volume.Id, scanStartedUtc, skipped, ct);

            // …and out of the search index with them, in the same transaction: a file that is no
            // longer on disk — or no longer inside the perimeter — must stop being a search hit.
            await _ftsIndex.PruneVolumeAsync(volume.Id, ct);
            await tx.CommitAsync(ct);

            if (closure.Absent > 0)
            {
                _logger.LogInformation(
                    "Volume {VolumeId}: {Count} file(s) no longer on disk, marked absent.", volume.Id, closure.Absent);
            }

            if (closure.Excluded > 0)
            {
                // Deliberately does NOT claim they are still on disk: the scan did not look there,
                // which is the whole reason their presence was left alone.
                // "Rows recorded", not "rows newly hidden": the pass writes the cause on every row
                // it applies to, and a row already excluded for another reason is counted here too
                // when it learns this one. Both are real writes; neither is a user-visible change
                // on its own.
                _logger.LogInformation(
                    "Volume {VolumeId}: {Count} row(s) recorded as outside the scanned perimeter " +
                    "(their presence is left as it was — this scan did not look there).",
                    volume.Id, closure.Excluded);
            }
        }

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            volume.LastFullScanUtc = scanStartedUtc;
            if (checkpoint is { } journal)
            {
                volume.LastUsn = journal.NextUsn;
                volume.UsnJournalId = unchecked((long)journal.JournalId);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    /// <summary>
    /// Translates the perimeter into the terms the file rows are addressed by: directory ids.
    ///
    /// <para>Every catalog directory of the volume is asked once whether the scan covered it —
    /// <paramref name="idByPath"/> is the map the directory merge has already built, so this costs
    /// no query — and the ones it did not cover become one skipped area each. Normally that set is
    /// EMPTY: the catalog only holds what was inside the perimeter when it was written, so a
    /// directory falls out of it only when the user narrows the perimeter (a root switched off, a
    /// folder made hidden), and only until they widen it again.</para>
    ///
    /// <para>The individually skipped files come last and only when their directory IS covered:
    /// one whose directory is outside the perimeter is already accounted for by the directory's
    /// own area, and a file row can only exist under a directory row.</para>
    /// </summary>
    private List<SkippedScanArea> SkippedAreas(
        Volume volume, ScanPerimeter perimeter, IReadOnlyDictionary<string, int> idByPath)
    {
        var areas = new List<SkippedScanArea>();

        foreach (var (path, id) in idByPath)
        {
            if (perimeter.SkipCause(path) is { } cause)
            {
                areas.Add(new SkippedScanArea(id, FileName: null, cause));
            }
        }

        foreach (var file in perimeter.SkippedFiles)
        {
            if (idByPath.TryGetValue(ScanPath.Parent(file), out var directoryId))
            {
                // FilteredOut by construction: an item outside every active root never reaches
                // ScanPerimeter.SkipFile, so a file on this list was offered to the filter and
                // refused by it — the cause no setting can retract.
                areas.Add(new SkippedScanArea(directoryId, ScanPath.Name(file), ScanSkipCause.FilteredOut));
                continue;
            }

            // Unreachable with a coherent catalog: a file the scan stepped over is inside an
            // active root, so its directory is either an existing row or one this scan just
            // inserted. Loud rather than silent (§9) because the consequence is precisely the bug
            // this pass exists to remove — the row would fall through to the absence pass and be
            // stamped "no longer on disk" for a file that is sitting there.
            _logger.LogWarning(
                "Volume {VolumeId}: skipped file '{Path}' has no directory row for '{Parent}'; " +
                "if it is in the catalog it will be flagged absent instead of excluded.",
                volume.Id, file, ScanPath.Parent(file));
        }

        return areas;
    }

    private static FileEntry ToEntity(
        int volumeId, ResolvedFile file, IReadOnlyDictionary<string, int> dirIdByPath, DateTime indexedUtc)
    {
        var parentPath = ScanPath.Parent(file.Item.RelativePath);
        if (!dirIdByPath.TryGetValue(parentPath, out var directoryId))
        {
            throw new InvalidOperationException(
                $"File '{file.Item.RelativePath}' on volume {volumeId} has no directory row for '{parentPath}'.");
        }

        return new FileEntry
        {
            VolumeId = volumeId,
            DirectoryId = directoryId,
            Name = file.Item.Name,
            Extension = file.Extension,
            Category = file.Category,
            SizeBytes = file.SizeBytes,
            FileCreatedUtc = file.CreatedUtc,
            FileModifiedUtc = file.ModifiedUtc,
            Attributes = file.Item.Attributes,
            UsnFileRef = file.Item.Frn is { } frn ? unchecked((long)frn) : null,
            IsIncluded = true,
            IsPresent = true,

            // The merge overwrites this in SQL; on the bulk-insert path it is the row's own
            // generation stamp, and without it the absent pass would sweep away the very rows
            // the scan just wrote.
            LastIndexedUtc = indexedUtc,
        };
    }

    /// <summary>
    /// The directory tree the scan saw: the synthetic root (""), every kept directory and
    /// every ancestor of a kept directory or file. Paths only — identities and parent wiring
    /// belong to <see cref="DirectoryMerger"/>, which reconciles them with the existing rows.
    /// </summary>
    private static List<ScannedDirectory> BuildDirectoryTree(
        List<ScanItem> dirItems,
        List<ResolvedFile> files)
    {
        var frnByPath = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirItems)
        {
            if (dir.Frn is { } frn)
            {
                frnByPath[dir.RelativePath] = frn;
            }
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };

        void Ensure(string path)
        {
            while (path.Length > 0 && paths.Add(path))
            {
                path = ScanPath.Parent(path);
            }
        }

        foreach (var dir in dirItems)
        {
            Ensure(dir.RelativePath);
        }

        foreach (var file in files)
        {
            Ensure(ScanPath.Parent(file.Item.RelativePath));
        }

        return paths
            .Select(p => new ScannedDirectory(
                p, frnByPath.TryGetValue(p, out var frn) ? unchecked((long)frn) : null))
            .ToList();
    }

    private sealed record ResolvedFile(
        ScanItem Item,
        long SizeBytes,
        DateTime CreatedUtc,
        DateTime ModifiedUtc,
        string Extension,
        FileCategory Category);
}
