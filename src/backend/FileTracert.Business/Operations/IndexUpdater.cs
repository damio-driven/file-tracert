using FileTracert.Business.Filtering;
using FileTracert.Business.Projection;
using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Keeps the <c>Files</c>/<c>Directories</c> rows and the FTS5 index consistent
/// with the physical outcome of a completed job. Called by <see cref="JobExecutionEngine"/>
/// immediately after the job reaches <see cref="JobState.Completed"/>.
/// </summary>
public sealed class IndexUpdater
{
    private readonly FileTracertDbContext _db;
    private readonly IFileSearchIndex _fts;
    private readonly DirectoryResolver _directories;
    private readonly RootFilterResolver _filters;
    private readonly ILogger<IndexUpdater> _logger;

    public IndexUpdater(
        FileTracertDbContext db,
        IFileSearchIndex fts,
        DirectoryResolver directories,
        RootFilterResolver filters,
        ILogger<IndexUpdater> logger)
    {
        _db = db;
        _fts = fts;
        _directories = directories;
        _filters = filters;
        _logger = logger;
    }

    public Task UpdateAfterCompletionAsync(OperationJob job, CancellationToken ct) =>
        UpdateAfterCompletionAsync(job, removedSourceDirPaths: [], ct);

    /// <param name="removedSourceDirPaths">Source directories the engine physically removed
    /// during a cross-volume MoveFolder — exactly these rows get de-materialized (#15).</param>
    public async Task UpdateAfterCompletionAsync(
        OperationJob job, IReadOnlyCollection<string> removedSourceDirPaths, CancellationToken ct)
    {
        _logger.LogDebug("IndexUpdater: updating index for job {Id} type={Type}.", job.Id, job.Type);

        switch (job.Type)
        {
            case JobType.CreateFolder:  await CreateFolderIndexAsync(job, ct); break;
            case JobType.RenameFile:    await RenameFileIndexAsync(job, ct); break;
            case JobType.RenameFolder:  await RenameFolderIndexAsync(job, ct); break;
            case JobType.MoveFile:      await MoveFileIndexAsync(job, ct); break;
            case JobType.MoveFolder:    await MoveFolderIndexAsync(job, removedSourceDirPaths, ct); break;
        }
    }

    // ── per-type handlers ─────────────────────────────────────────────────────

    private async Task CreateFolderIndexAsync(OperationJob job, CancellationToken ct)
    {
        if (job.TargetVolumeId is null || job.TargetRelativePath is null) return;
        await FindOrCreateDirAsync(job.TargetVolumeId.Value, job.TargetRelativePath, ct);
    }

    /// <summary>
    /// C19. A rename used to write the new <c>Name</c> and stop there, so <c>Extension</c> and
    /// <c>Category</c> kept describing the OLD name until the next full re-scan — and everything
    /// that filters on them (the search facets, <c>FilterReconciler</c>) worked on dead values:
    /// <c>foto.jpg</c> renamed to <c>foto.txt</c> stayed an Image with extension <c>jpg</c>.
    ///
    /// Both are re-derived with the SAME helpers the scan pipeline uses (§9, no second rule
    /// written by hand here), and inclusion is reconciled the way §4 requires: a name that
    /// leaves the allow-list flips <c>IsIncluded</c> to false — never a delete — and one that
    /// comes back in is re-included. The search index follows in the same direction, because an
    /// excluded row that stayed in FTS would still be a hit.
    ///
    /// <para><b>Known gap:</b> <c>ShouldIncludeFile</c> is no longer the WHOLE scan rule — since
    /// the inherited-exclusion fix a scan also drops anything under an excluded folder. A rename
    /// of such a file would re-include it here. Narrow, because after that fix no such row is
    /// written any more; it can only be met in a catalog an older build produced, and the next
    /// scan puts it back.</para>
    /// </summary>
    private async Task RenameFileIndexAsync(OperationJob job, CancellationToken ct)
    {
        var item = job.Items.FirstOrDefault();
        if (item?.FileId is null) return;

        var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == item.FileId.Value, ct);
        if (file is null) return;

        var newName = ScanPath.Name(item.TargetRelativePath);
        var extension = FileFilter.GetExtension(newName);
        var categories = await _db.ExtensionCategories.AsNoTracking()
            .ToDictionaryAsync(e => e.Extension, e => e.Category, ct);
        var filter = await _filters.ResolveForPathAsync(file.VolumeId, item.TargetRelativePath, ct);

        file.Name = newName;
        file.Extension = extension;
        file.Category = FileFilter.ResolveCategory(extension, categories);
        file.IsIncluded = FileFilter.ShouldIncludeFile(
            item.TargetRelativePath, extension, file.Attributes, filter);

        await _db.SaveChangesAsync(ct);

        // Both flags, because both are what the index itself requires (FileSearchIndex's
        // IndexableSql): a row still flagged absent — the refresher lets those through on purpose
        // — would otherwise get an entry the next prune immediately deletes.
        if (file.IsIncluded && file.IsPresent)
            await _fts.UpsertAsync(file.Id, file.Name, item.TargetRelativePath, ct);
        else
            await _fts.RemoveAsync(file.Id, ct);
    }

    private async Task RenameFolderIndexAsync(OperationJob job, CancellationToken ct)
    {
        var item = job.Items.FirstOrDefault();
        if (item is null || job.SourceVolumeId is null) return;

        await CascadeDirRenameAsync(job.SourceVolumeId.Value,
            item.SourceRelativePath, item.TargetRelativePath, ct);
    }

    private async Task MoveFileIndexAsync(OperationJob job, CancellationToken ct)
    {
        var item = job.Items.FirstOrDefault();
        if (item?.FileId is null) return;

        var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == item.FileId.Value, ct);
        if (file is null) return;

        var targetVolumeId = job.TargetVolumeId ?? file.VolumeId;
        var targetDirPath = ScanPath.Parent(item.TargetRelativePath);
        var targetDir = await FindOrCreateDirAsync(targetVolumeId, targetDirPath, ct);

        file.DirectoryId = targetDir.Id;
        if (!job.IsIntraVolume)
            RepointToVolume(file, targetVolumeId);

        await _db.SaveChangesAsync(ct);
        await _fts.UpsertAsync(file.Id, file.Name, item.TargetRelativePath, ct);
    }

    private async Task MoveFolderIndexAsync(
        OperationJob job, IReadOnlyCollection<string> removedSourceDirPaths, CancellationToken ct)
    {
        if (job.IsIntraVolume)
            await MoveFolderIntraIndexAsync(job, ct);
        else
            await MoveFolderCrossIndexAsync(job, removedSourceDirPaths, ct);
    }

    private async Task MoveFolderIntraIndexAsync(OperationJob job, CancellationToken ct)
    {
        var item = job.Items.FirstOrDefault();
        if (item is null || job.SourceVolumeId is null) return;

        var oldPath = item.SourceRelativePath;
        var newPath = item.TargetRelativePath;

        // Load all dirs in the subtree.
        var dirs = await _db.Directories
            .InSubtree(job.SourceVolumeId.Value, oldPath)
            .ToListAsync(ct);

        var topDir = dirs.FirstOrDefault(d => ScanPath.SamePath(d.MaterializedPath, oldPath));
        if (topDir is null) return;

        // Re-parent the top directory.
        var newParentPath = ScanPath.Parent(newPath);
        if (string.IsNullOrEmpty(newParentPath))
        {
            topDir.ParentId = null;
        }
        else
        {
            var newParent = await FindOrCreateDirAsync(job.SourceVolumeId.Value, newParentPath, ct);
            topDir.ParentId = newParent.Id;
        }

        // Cascade MaterializedPath across the whole subtree.
        foreach (var d in dirs)
            d.MaterializedPath = newPath + d.MaterializedPath[oldPath.Length..];

        await _db.SaveChangesAsync(ct);
        await UpdateFtsForDirsAsync(dirs, ct);
    }

    private async Task MoveFolderCrossIndexAsync(
        OperationJob job, IReadOnlyCollection<string> removedSourceDirPaths, CancellationToken ct)
    {
        if (job.TargetVolumeId is null) return;
        var targetVolumeId = job.TargetVolumeId.Value;

        // Materialize the target tree the engine physically created: every source subtree
        // directory row gets its mapped counterpart under the destination root. Done BEFORE
        // de-materializing the source rows, which are the mapping input.
        var marker = job.Items.FirstOrDefault(i =>
            i.FileId is null &&
            string.Equals(i.TargetRelativePath, job.TargetRelativePath, StringComparison.OrdinalIgnoreCase));
        if (marker is not null && job.SourceVolumeId is not null)
        {
            var srcRoot = marker.SourceRelativePath;
            var srcDirPaths = await _db.Directories.AsNoTracking()
                .InSubtree(job.SourceVolumeId.Value, srcRoot)
                .Select(d => d.MaterializedPath)
                .ToListAsync(ct);

            foreach (var srcDirPath in srcDirPaths)
            {
                var mapped = marker.TargetRelativePath + srcDirPath[srcRoot.Length..];
                var dir = await FindOrCreateDirAsync(targetVolumeId, mapped, ct);
                dir.IsMaterialized = true; // physically created by the engine
            }
        }

        var fileItems = job.Items.Where(i => i.FileId.HasValue).ToList();

        // Load every affected file up front instead of one query per item.
        var fileIds = fileItems.Select(i => i.FileId!.Value).ToList();
        var files = await _db.Files
            .Where(f => fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        // Resolve each distinct target directory once (a big folder move lands thousands of files
        // into a handful of directories).
        var dirCache = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase);
        var movedFileIds = new List<int>(fileItems.Count);

        foreach (var item in fileItems)
        {
            if (!files.TryGetValue(item.FileId!.Value, out var file)) continue;

            var targetDirPath = ScanPath.Parent(item.TargetRelativePath);
            if (!dirCache.TryGetValue(targetDirPath, out var targetDir))
            {
                targetDir = await FindOrCreateDirAsync(targetVolumeId, targetDirPath, ct);
                dirCache[targetDirPath] = targetDir;
            }

            RepointToVolume(file, targetVolumeId);
            file.DirectoryId = targetDir.Id;
            movedFileIds.Add(file.Id);
        }

        // #15: de-materialize exactly the source directories the engine physically removed.
        // The Catalog tree filters on IsMaterialized, so the recycled subtree stops being
        // navigable; rows are kept (no hard-delete, §6) so soft-deleted file rows keep a
        // valid FK. Directories left behind with uncopied content are NOT in the list and
        // stay visible.
        if (removedSourceDirPaths.Count > 0 && job.SourceVolumeId is not null)
        {
            var removedSet = new HashSet<string>(removedSourceDirPaths, StringComparer.OrdinalIgnoreCase);
            // The removed paths all sit under the moved folder's root (the shortest path in
            // the set) — bound the query to that subtree instead of scanning the volume.
            var subtreeRoot = removedSourceDirPaths.OrderBy(p => p.Length).First();
            var candidates = await _db.Directories
                .InSubtree(job.SourceVolumeId.Value, subtreeRoot)
                .ToListAsync(ct);
            foreach (var ghost in candidates.Where(d => removedSet.Contains(d.MaterializedPath)))
                ghost.IsMaterialized = false;
        }

        // C5: one round-trip for the whole batch of file re-points, not one SaveChanges per file.
        await _db.SaveChangesAsync(ct);

        // E4 — set-based, and AFTER the save: the index entry is rebuilt from the rows as they now
        // stand, so the directory each file was just re-pointed to is the one the path is built
        // from. A folder move of 50 000 files was 100 000 statements here; it is now 2 per 500.
        await _fts.SyncFilesAsync(movedFileIds, ct);
    }

    /// <summary>
    /// FIX #14: reconciles a cancelled job's items that already "landed" on the target.
    /// A Verified item has its copy finalized under the real name; a Done item has
    /// additionally lost its source to the recycle bin. Both represent files that
    /// physically live on the target now — re-pointing their <c>Files</c> rows (and FTS)
    /// keeps the completed work indexed instead of orphaning it, and stops the Catalog
    /// from showing a source-side ghost for Done items. A Verified item's source file
    /// still exists physically; the next scan re-indexes it as a new row. Safe to call
    /// more than once (engine and API cancel paths may both run it).
    /// </summary>
    public async Task ReconcileCancelledJobAsync(OperationJob job, CancellationToken ct)
    {
        if (job.TargetVolumeId is null || job.IsIntraVolume) return;
        var targetVolumeId = job.TargetVolumeId.Value;

        var landed = job.Items
            .Where(i => i.State is JobItemState.Verified or JobItemState.Done)
            .ToList();
        if (landed.Count == 0) return;

        var dirCache = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase);
        var landedFileIds = new List<int>(landed.Count);
        foreach (var item in landed)
        {
            if (item.FileId is null)
            {
                // Folder marker (target == the job's destination root): the target root
                // directory was physically created — index it. Any other FileId-less item
                // (legacy file item) cannot be re-pointed and is skipped.
                if (string.Equals(item.TargetRelativePath, job.TargetRelativePath, StringComparison.OrdinalIgnoreCase))
                    await FindOrCreateDirAsync(targetVolumeId, item.TargetRelativePath, ct);
                continue;
            }

            var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == item.FileId.Value, ct);
            if (file is null) continue;

            var targetDirPath = ScanPath.Parent(item.TargetRelativePath);
            if (!dirCache.TryGetValue(targetDirPath, out var targetDir))
            {
                targetDir = await FindOrCreateDirAsync(targetVolumeId, targetDirPath, ct);
                dirCache[targetDirPath] = targetDir;
            }

            RepointToVolume(file, targetVolumeId);
            file.DirectoryId = targetDir.Id;
            landedFileIds.Add(file.Id);
        }

        await _db.SaveChangesAsync(ct);

        // E4 — one set-based sync instead of a DELETE + INSERT per landed item, and now AFTER the
        // save rather than before it: the old order rebuilt each entry from rows whose new
        // DirectoryId had not been written yet, so the index was correct only because the path was
        // also passed in by hand. Reading it from the saved rows removes that coincidence.
        await _fts.SyncFilesAsync(landedFileIds, ct);

        _logger.LogInformation(
            "Job {Id}: reconciled {Count} landed item(s) to the target index after cancel.",
            job.Id, landed.Count);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves a file row to another volume (finding 6). The USN file reference is the file's
    /// identity <em>inside one volume</em> — a low MFT index repeats on every NTFS volume — so
    /// carrying the source FRN across would (a) risk the unique <c>(VolumeId, UsnFileRef)</c>
    /// index firing AFTER the bytes were already moved, flipping a successful job to Failed, and
    /// (b) leave a stale FRN that poisons the very matching the scan merge tries first (step 9a)
    /// and the incremental USN delta. Cleared here, in the SAME SaveChanges that re-points the
    /// row: a separate transaction would leave a window in which the violation can still fire.
    /// The next scan of the target re-assigns it (match by FRN, then by path COLLATE NOCASE).
    ///
    /// <para><c>QuickHash</c>/<c>Hash</c> deliberately survive: they are a function of the file's
    /// CONTENT, not of where it lives, and the only place that reads them
    /// (<c>BulkIndexWriter.ScanMerge</c>) treats them as facts a scan never re-derives.</para>
    /// </summary>
    private static void RepointToVolume(FileEntry file, int targetVolumeId)
    {
        file.VolumeId = targetVolumeId;
        file.UsnFileRef = null;
    }

    /// <summary>
    /// Finds or recursively creates all directories in <paramref name="path"/> on the given volume,
    /// as folders that now exist ON DISK. Delegates to <see cref="DirectoryResolver"/>: the enqueue
    /// creates the very same rows as mere projection placeholders (§5), and this call is what
    /// promotes them to materialized once the engine really created them — one walk, one set of
    /// flags, no second row appearing next to the first.
    /// </summary>
    private Task<DirectoryNode> FindOrCreateDirAsync(int volumeId, string path, CancellationToken ct) =>
        _directories.FindOrCreateMaterializedAsync(volumeId, path, ct);

    /// <summary>Cascades a directory rename/move across the subtree and updates FTS.</summary>
    private async Task CascadeDirRenameAsync(int volumeId, string oldPath, string newPath, CancellationToken ct)
    {
        var dirs = await _db.Directories
            .InSubtree(volumeId, oldPath)
            .ToListAsync(ct);

        var topDir = dirs.FirstOrDefault(d => ScanPath.SamePath(d.MaterializedPath, oldPath));
        if (topDir is not null)
            topDir.Name = ScanPath.Name(newPath);

        foreach (var d in dirs)
            d.MaterializedPath = newPath + d.MaterializedPath[oldPath.Length..];

        await _db.SaveChangesAsync(ct);
        await UpdateFtsForDirsAsync(dirs, ct);
    }

    /// <summary>
    /// Re-syncs the search index for every file of the given directories, whose new
    /// <c>MaterializedPath</c> the caller has already saved.
    ///
    /// <para>E4 — set-based. This used to load the full <c>FileEntry</c> of every file in the
    /// subtree and then call <c>UpsertAsync</c> per file, which is a DELETE and an INSERT each: a
    /// folder rename over 50 000 files meant 50 000 entities on the heap and 100 000 statements.
    /// <see cref="IFileSearchIndex.SyncFilesAsync"/> does the same job in chunks of 500 with an
    /// <c>INSERT … SELECT</c>, so the count drops to 2 statements per 500 files (200 instead of
    /// 100 000 here) and only the ids ever cross the boundary.</para>
    ///
    /// <para>It also stops re-deriving the indexed name and path in C#. The rebuilt entry is
    /// computed by the SQL that owns that rule (§3, §5: the <c>name</c> column is the PROJECTED
    /// name, <c>COALESCE(NULLIF(PendingName, ''), Name)</c>), which is the same expression the
    /// scan and the full rebuild use — one definition instead of a fourth hand-written copy that
    /// could drift. The path it builds is the directory's saved <c>MaterializedPath</c> joined
    /// with that name, i.e. exactly what this method used to assemble by hand.</para>
    /// </summary>
    private async Task UpdateFtsForDirsAsync(List<DirectoryNode> dirs, CancellationToken ct)
    {
        var dirIds = dirs.Select(d => d.Id).ToHashSet();

        // Ids only — the whole point is that no file row is materialised. The inclusion filter is
        // left to SyncFilesAsync, which removes the entry of anything not indexable instead of
        // silently leaving a stale one behind.
        var fileIds = await _db.Files.AsNoTracking()
            .Where(f => dirIds.Contains(f.DirectoryId))
            .Select(f => f.Id)
            .ToListAsync(ct);

        await _fts.SyncFilesAsync(fileIds, ct);
    }

}
