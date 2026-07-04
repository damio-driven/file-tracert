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
    private readonly ILogger<IndexUpdater> _logger;

    public IndexUpdater(FileTracertDbContext db, IFileSearchIndex fts, ILogger<IndexUpdater> logger)
    {
        _db = db;
        _fts = fts;
        _logger = logger;
    }

    public async Task UpdateAfterCompletionAsync(OperationJob job, CancellationToken ct)
    {
        _logger.LogDebug("IndexUpdater: updating index for job {Id} type={Type}.", job.Id, job.Type);

        switch (job.Type)
        {
            case JobType.CreateFolder:  await CreateFolderIndexAsync(job, ct); break;
            case JobType.RenameFile:    await RenameFileIndexAsync(job, ct); break;
            case JobType.RenameFolder:  await RenameFolderIndexAsync(job, ct); break;
            case JobType.MoveFile:      await MoveFileIndexAsync(job, ct); break;
            case JobType.MoveFolder:    await MoveFolderIndexAsync(job, ct); break;
        }
    }

    // ── per-type handlers ─────────────────────────────────────────────────────

    private async Task CreateFolderIndexAsync(OperationJob job, CancellationToken ct)
    {
        if (job.TargetVolumeId is null || job.TargetRelativePath is null) return;
        await FindOrCreateDirAsync(job.TargetVolumeId.Value, job.TargetRelativePath, ct);
    }

    private async Task RenameFileIndexAsync(OperationJob job, CancellationToken ct)
    {
        var item = job.Items.FirstOrDefault();
        if (item?.FileId is null) return;

        var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == item.FileId.Value, ct);
        if (file is null) return;

        file.Name = ScanPath.Name(item.TargetRelativePath);
        await _db.SaveChangesAsync(ct);
        await _fts.UpsertAsync(file.Id, file.Name, item.TargetRelativePath, ct);
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
            file.VolumeId = targetVolumeId;

        await _db.SaveChangesAsync(ct);
        await _fts.UpsertAsync(file.Id, file.Name, item.TargetRelativePath, ct);
    }

    private async Task MoveFolderIndexAsync(OperationJob job, CancellationToken ct)
    {
        if (job.IsIntraVolume)
            await MoveFolderIntraIndexAsync(job, ct);
        else
            await MoveFolderCrossIndexAsync(job, ct);
    }

    private async Task MoveFolderIntraIndexAsync(OperationJob job, CancellationToken ct)
    {
        var item = job.Items.FirstOrDefault();
        if (item is null || job.SourceVolumeId is null) return;

        var oldPath = item.SourceRelativePath;
        var newPath = item.TargetRelativePath;

        // Load all dirs in the subtree.
        var dirs = await _db.Directories
            .Where(d => d.VolumeId == job.SourceVolumeId.Value &&
                        (d.MaterializedPath == oldPath || d.MaterializedPath.StartsWith(oldPath + "\\")))
            .ToListAsync(ct);

        var topDir = dirs.FirstOrDefault(d => d.MaterializedPath == oldPath);
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

    private async Task MoveFolderCrossIndexAsync(OperationJob job, CancellationToken ct)
    {
        if (job.TargetVolumeId is null) return;
        var targetVolumeId = job.TargetVolumeId.Value;

        var fileItems = job.Items.Where(i => i.FileId.HasValue).ToList();
        if (fileItems.Count == 0) return;

        // Load every affected file up front instead of one query per item.
        var fileIds = fileItems.Select(i => i.FileId!.Value).ToList();
        var files = await _db.Files
            .Where(f => fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        // Resolve each distinct target directory once (a big folder move lands thousands of files
        // into a handful of directories).
        var dirCache = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase);
        var ftsUpserts = new List<(int Id, string Name, string Path)>(fileItems.Count);

        foreach (var item in fileItems)
        {
            if (!files.TryGetValue(item.FileId!.Value, out var file)) continue;

            var targetDirPath = ScanPath.Parent(item.TargetRelativePath);
            if (!dirCache.TryGetValue(targetDirPath, out var targetDir))
            {
                targetDir = await FindOrCreateDirAsync(targetVolumeId, targetDirPath, ct);
                dirCache[targetDirPath] = targetDir;
            }

            file.VolumeId = targetVolumeId;
            file.DirectoryId = targetDir.Id;
            ftsUpserts.Add((file.Id, file.Name, item.TargetRelativePath));
        }

        // C5: one round-trip for the whole batch of file re-points, not one SaveChanges per file.
        await _db.SaveChangesAsync(ct);

        foreach (var (id, name, path) in ftsUpserts)
            await _fts.UpsertAsync(id, name, path, ct);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Finds or recursively creates all directories in <paramref name="path"/> on the given volume.</summary>
    private async Task<DirectoryNode> FindOrCreateDirAsync(int volumeId, string path, CancellationToken ct)
    {
        var existing = await _db.Directories
            .FirstOrDefaultAsync(d => d.VolumeId == volumeId && d.MaterializedPath == path, ct);
        if (existing is not null) return existing;

        var name = string.IsNullOrEmpty(path) ? string.Empty : ScanPath.Name(path);
        var parentPath = ScanPath.Parent(path);

        DirectoryNode? parent = null;
        if (!string.IsNullOrEmpty(path))  // non-root
            parent = await FindOrCreateDirAsync(volumeId, parentPath, ct);

        var newDir = new DirectoryNode
        {
            VolumeId = volumeId,
            ParentId = parent?.Id,
            Name = name,
            MaterializedPath = path,
            IsMaterialized = true
        };
        _db.Directories.Add(newDir);
        await _db.SaveChangesAsync(ct);
        return newDir;
    }

    /// <summary>Cascades a directory rename/move across the subtree and updates FTS.</summary>
    private async Task CascadeDirRenameAsync(int volumeId, string oldPath, string newPath, CancellationToken ct)
    {
        var dirs = await _db.Directories
            .Where(d => d.VolumeId == volumeId &&
                        (d.MaterializedPath == oldPath || d.MaterializedPath.StartsWith(oldPath + "\\")))
            .ToListAsync(ct);

        var topDir = dirs.FirstOrDefault(d => d.MaterializedPath == oldPath);
        if (topDir is not null)
            topDir.Name = ScanPath.Name(newPath);

        foreach (var d in dirs)
            d.MaterializedPath = newPath + d.MaterializedPath[oldPath.Length..];

        await _db.SaveChangesAsync(ct);
        await UpdateFtsForDirsAsync(dirs, ct);
    }

    /// <summary>Updates FTS path entries for all files in the given directories (already updated in memory).</summary>
    private async Task UpdateFtsForDirsAsync(List<DirectoryNode> dirs, CancellationToken ct)
    {
        var dirIds = dirs.Select(d => d.Id).ToHashSet();
        var files = await _db.Files.AsNoTracking()
            .Where(f => f.IsPresent && f.IsIncluded && dirIds.Contains(f.DirectoryId))
            .ToListAsync(ct);

        var pathById = dirs.ToDictionary(d => d.Id, d => d.MaterializedPath);

        foreach (var f in files)
        {
            if (!pathById.TryGetValue(f.DirectoryId, out var dirPath)) continue;
            await _fts.UpsertAsync(f.Id, f.Name, ScanPath.Join(dirPath, f.Name), ct);
        }
    }

}
