using System.ComponentModel;
using System.Text.Json;
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
        long? checkpointUsn = null;
        if (VolumeMapper.EngineFor(volume.FileSystem) == VolumeScanEngine.UsnJournal)
        {
            try
            {
                _usnReader.EnsureJournal(volume.VolumeGuid);
                checkpointUsn = _usnReader.GetJournalState(volume.VolumeGuid).NextUsn;
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
        var (dirItems, fileItems) = GatherAndFilter(volume, mountRoot, roots, filters, ct);

        _statusTracker.SetPhase(volumeId, ScanPhase.ReadingMetadata);
        var resolvedFiles = await ResolveFilesAsync(volume, mountRoot, fileItems, categoryMap, ct);

        _statusTracker.SetPhase(volumeId, ScanPhase.Writing);
        await PersistAsync(volume, dirItems, resolvedFiles, checkpointUsn, scanStartedUtc, ct);

        _logger.LogInformation(
            "Scanned volume {VolumeId}: {Dirs} directories, {Files} files.",
            volumeId, dirItems.Count, resolvedFiles.Count);
    }

    /// <summary>
    /// Resolves the effective filter once per watched root. A malformed override JSON
    /// is not silently ignored: it is logged, raised as a user-visible notification,
    /// and the root falls back to the default filter so the scan still proceeds.
    /// </summary>
    private async Task<Dictionary<string, EffectiveFilter>> ResolveRootFiltersAsync(
        Volume volume,
        List<WatchedRoot> roots,
        AppSettings? settings,
        CancellationToken ct)
    {
        var effectiveSettings = settings ?? new AppSettings();
        var filters = new Dictionary<string, EffectiveFilter>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var key = ScanPath.Normalize(root.RelativePath);
            try
            {
                filters[key] = EffectiveFilterBuilder.Build(effectiveSettings, root.FilterOverrideJson);
            }
            catch (JsonException ex)
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

                filters[key] = EffectiveFilterBuilder.Build(effectiveSettings, filterOverrideJson: null);
            }
        }

        return filters;
    }

    private (List<ScanItem> Dirs, List<ScanItem> Files) GatherAndFilter(
        Volume volume,
        string mountRoot,
        List<WatchedRoot> roots,
        IReadOnlyDictionary<string, EffectiveFilter> filters,
        CancellationToken ct)
    {
        var rootKeys = filters.Keys.ToList();

        var dirs = new List<ScanItem>();
        var files = new List<ScanItem>();

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
        var excluded = new ExcludedSubtrees();

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
            var rootKey = RootFilterResolver.MostSpecificRoot(rootKeys, item.RelativePath);
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
                    excluded.Add(item.RelativePath);
                }
            }
            else
            {
                var extension = FileFilter.GetExtension(item.Name);
                if (FileFilter.ShouldIncludeFile(item.RelativePath, extension, item.Attributes, filter))
                {
                    files.Add(item);
                }
            }
        }

        _statusTracker.ReportSeen(volume.Id, seen);
        DropExcludedSubtrees(volume, dirs, files, excluded);
        return (dirs, files);
    }

    /// <summary>
    /// Second pass of the C16 rule: drop what survived its own attributes but lives under a
    /// folder that did not. Dropping the descendant FILES is what also stops the ancestor walk
    /// in <see cref="BuildDirectoryTree"/> from resurrecting the excluded folder as a
    /// materialized row — a file the scan never keeps cannot create its own ancestors.
    /// </summary>
    private void DropExcludedSubtrees(
        Volume volume, List<ScanItem> dirs, List<ScanItem> files, ExcludedSubtrees excluded)
    {
        if (excluded.Count == 0)
        {
            return;
        }

        var droppedDirs = dirs.RemoveAll(d => excluded.Covers(d.RelativePath));
        var droppedFiles = files.RemoveAll(f => excluded.Covers(f.RelativePath));

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
        long? checkpointUsn,
        DateTime scanStartedUtc,
        CancellationToken ct)
    {
        var scannedDirs = BuildDirectoryTree(dirItems, files);
        var merge = await _directoryMerger.MergeAsync(volume.Id, scannedDirs, FileBatchSize, ct);

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

        // Everything the scan did not touch this run is gone from disk — flagged, never
        // deleted (§6), so a queued operation still finds the row it references.
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            var absent = await _bulkWriter.MarkAbsentFilesAsync(volume.Id, scanStartedUtc, ct);

            // …and out of the search index with them, in the same transaction: a file that is
            // no longer on disk must stop being a search hit.
            await _ftsIndex.PruneVolumeAsync(volume.Id, ct);
            await tx.CommitAsync(ct);

            if (absent > 0)
            {
                _logger.LogInformation(
                    "Volume {VolumeId}: {Count} file(s) no longer on disk, marked absent.", volume.Id, absent);
            }
        }

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            volume.LastFullScanUtc = scanStartedUtc;
            if (checkpointUsn is { } usn)
            {
                volume.LastUsn = usn;
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
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
