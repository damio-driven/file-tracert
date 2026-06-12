using FileTracert.Business.Filtering;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
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
    private readonly ILogger<ScanService> _logger;

    public ScanService(
        FileTracertDbContext db,
        IVolumeProbe probe,
        IUsnReader usnReader,
        IDirectoryEnumerator enumerator,
        IFileMetadataReader metadataReader,
        IBulkIndexWriter bulkWriter,
        ILogger<ScanService> logger)
    {
        _db = db;
        _probe = probe;
        _usnReader = usnReader;
        _enumerator = enumerator;
        _metadataReader = metadataReader;
        _bulkWriter = bulkWriter;
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

        var settings = await _db.AppSettings.FirstOrDefaultAsync(ct);
        var categoryMap = await _db.ExtensionCategories.ToDictionaryAsync(e => e.Extension, e => e.Category, ct);

        // For NTFS, checkpoint the journal position BEFORE reading the snapshot so
        // the future incremental catches everything that changed during the scan.
        long? checkpointUsn = volume.ScanEngine == VolumeScanEngine.UsnJournal
            ? _usnReader.GetJournalState(volume.VolumeGuid).NextUsn
            : null;

        var (dirItems, fileItems) = GatherAndFilter(volume, mountRoot, roots, settings);
        var resolvedFiles = await ResolveFilesAsync(volume, mountRoot, fileItems, categoryMap, ct);

        await PersistAsync(volume, dirItems, resolvedFiles, checkpointUsn, ct);

        _logger.LogInformation(
            "Scanned volume {VolumeId}: {Dirs} directories, {Files} files.",
            volumeId, dirItems.Count, resolvedFiles.Count);
    }

    private (List<ScanItem> Dirs, List<ScanItem> Files) GatherAndFilter(
        Volume volume,
        string mountRoot,
        List<WatchedRoot> roots,
        AppSettings? settings)
    {
        // Resolve the effective filter once per watched root.
        var filters = roots.ToDictionary(
            r => ScanPath.Normalize(r.RelativePath),
            r => EffectiveFilterBuilder.Build(settings ?? new AppSettings(), r.FilterOverrideJson),
            StringComparer.OrdinalIgnoreCase);
        var rootKeys = filters.Keys.ToList();

        var dirs = new List<ScanItem>();
        var files = new List<ScanItem>();

        foreach (var item in EnumerateRaw(volume, mountRoot, roots))
        {
            // Find the most specific active root that contains this item.
            var rootKey = rootKeys
                .Where(k => ScanPath.IsWithin(item.RelativePath, k))
                .OrderByDescending(k => k.Length)
                .FirstOrDefault();
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

        return (dirs, files);
    }

    private IEnumerable<ScanItem> EnumerateRaw(Volume volume, string mountRoot, List<WatchedRoot> roots)
    {
        if (volume.ScanEngine == VolumeScanEngine.UsnJournal)
        {
            // USN enumerates the whole volume; root filtering happens upstream.
            foreach (var e in _usnReader.ReadFullSnapshot(volume.VolumeGuid, CancellationToken.None))
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
            foreach (var e in _enumerator.Enumerate(mountRoot, root.RelativePath, CancellationToken.None))
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

    private async Task PersistAsync(
        Volume volume,
        List<ScanItem> dirItems,
        List<ResolvedFile> files,
        long? checkpointUsn,
        CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Defer FK checks to commit so we can truncate (self-referencing tree) and
        // reinsert in any order within the transaction.
        await _db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys=ON;", ct);

        // Idempotent re-scan: replace this volume's index.
        await _db.Files.Where(f => f.VolumeId == volume.Id).ExecuteDeleteAsync(ct);
        await _db.Directories.Where(d => d.VolumeId == volume.Id).ExecuteDeleteAsync(ct);

        var nodeByPath = BuildDirectoryTree(volume.Id, dirItems, files);

        // Directories go through EF (few rows; self-referencing identity/order is
        // handled by the change tracker), files through the bulk hot path.
        _db.Directories.AddRange(nodeByPath.Values);
        await _db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        var fileEntities = files.Select(f => new FileEntry
        {
            VolumeId = volume.Id,
            DirectoryId = nodeByPath[ScanPath.Parent(f.Item.RelativePath)].Id,
            Name = f.Item.Name,
            Extension = f.Extension,
            Category = f.Category,
            SizeBytes = f.SizeBytes,
            FileCreatedUtc = f.CreatedUtc,
            FileModifiedUtc = f.ModifiedUtc,
            Attributes = f.Item.Attributes,
            UsnFileRef = f.Item.Frn is { } frn ? unchecked((long)frn) : null,
            IsIncluded = true,
            IsPresent = true,
            LastIndexedUtc = now,
        }).ToList();

        await _bulkWriter.BulkInsertFilesAsync(fileEntities, ct);

        volume.LastFullScanUtc = now;
        if (checkpointUsn is { } usn)
        {
            volume.LastUsn = usn;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Builds the directory nodes (keyed by relative path) for the volume,
    /// including the synthetic root ("") and every ancestor of a kept directory
    /// or file, wiring the <see cref="DirectoryNode.Parent"/> navigation.
    /// </summary>
    private static Dictionary<string, DirectoryNode> BuildDirectoryTree(
        int volumeId,
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

        var nodeByPath = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new DirectoryNode
            {
                VolumeId = volumeId,
                Name = string.Empty,
                MaterializedPath = string.Empty,
                ParentId = null,
                IsMaterialized = true,
            },
        };

        void Ensure(string path)
        {
            if (path.Length == 0 || nodeByPath.ContainsKey(path))
            {
                return;
            }

            var parent = ScanPath.Parent(path);
            Ensure(parent);

            nodeByPath[path] = new DirectoryNode
            {
                VolumeId = volumeId,
                Name = ScanPath.Name(path),
                MaterializedPath = path,
                Parent = nodeByPath[parent],
                IsMaterialized = true,
                UsnFileRef = frnByPath.TryGetValue(path, out var frn) ? unchecked((long)frn) : null,
            };
        }

        foreach (var dir in dirItems)
        {
            Ensure(dir.RelativePath);
        }

        foreach (var file in files)
        {
            Ensure(ScanPath.Parent(file.Item.RelativePath));
        }

        return nodeByPath;
    }

    private sealed record ResolvedFile(
        ScanItem Item,
        long SizeBytes,
        DateTime CreatedUtc,
        DateTime ModifiedUtc,
        string Extension,
        FileCategory Category);
}
