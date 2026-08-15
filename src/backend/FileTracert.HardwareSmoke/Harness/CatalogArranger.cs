using FileTracert.Business.Filtering;
using FileTracert.Business.Scanning;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.HardwareSmoke.Harness;

/// <summary>
/// Indexes an arranged fixture tree into the harness database, so the queue operates on a catalog
/// that mirrors what a real scan would have produced.
///
/// It walks the tree through the real <see cref="IDirectoryEnumerator"/> platform port and decides
/// inclusion with the real <see cref="FileFilter"/>/<see cref="EffectiveFilter"/> from Business —
/// so "excluded file" in a scenario means excluded by the product's own filter, not by a flag the
/// harness invented. The rows and the FTS entries are then written through EF and the real
/// <see cref="IFileSearchIndex"/>.
///
/// It deliberately does NOT call <c>ScanService</c>: on an NTFS volume that service enumerates the
/// whole MFT — correct for the product, minutes per volume for a per-scenario fixture of a dozen
/// files. (Since step 9a the scan merges instead of truncating, so re-indexing is no longer
/// destructive; the scenario that needs the real scan runs it explicitly — see
/// <c>rescan-preserves-overlay</c>.)
/// </summary>
public sealed class CatalogArranger
{
    private readonly FileTracertDbContext _db;
    private readonly IDirectoryEnumerator _enumerator;
    private readonly IFileSearchIndex _fts;

    public CatalogArranger(FileTracertDbContext db, IDirectoryEnumerator enumerator, IFileSearchIndex fts)
    {
        _db = db;
        _enumerator = enumerator;
        _fts = fts;
    }

    /// <summary>Result of indexing one fixture root.</summary>
    /// <param name="IndexedFiles">Volume-relative paths that made it into the catalog.</param>
    /// <param name="ExcludedFiles">Volume-relative paths the filter rejected — present on disk, absent from the catalog.</param>
    public sealed record IndexResult(IReadOnlyList<string> IndexedFiles, IReadOnlyList<string> ExcludedFiles);

    /// <summary>
    /// Indexes everything under <paramref name="volumeRelativeRoot"/> on the given volume.
    /// Safe to call more than once per volume (different roots): existing directory rows are
    /// reused instead of duplicated.
    /// </summary>
    public async Task<IndexResult> IndexAsync(
        int volumeId,
        string mountPoint,
        string volumeRelativeRoot,
        EffectiveFilter filter,
        CancellationToken ct)
    {
        var categoryMap = await _db.ExtensionCategories
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Extension, e => e.Category, ct);

        var byPath = await LoadExistingDirectoriesAsync(volumeId, ct);

        var includedFiles = new List<ScanEntry>();
        var excludedFiles = new List<string>();
        var includedDirs = new List<ScanEntry>();

        foreach (var entry in _enumerator.Enumerate(mountPoint, volumeRelativeRoot, ct))
        {
            var relativePath = ScanPath.Normalize(entry.RelativePath);

            if (entry.IsDirectory)
            {
                if (FileFilter.ShouldIncludeDirectory(relativePath, entry.Attributes, filter))
                    includedDirs.Add(entry with { RelativePath = relativePath });
                continue;
            }

            var extension = FileFilter.GetExtension(entry.Name);
            if (FileFilter.ShouldIncludeFile(relativePath, extension, entry.Attributes, filter))
                includedFiles.Add(entry with { RelativePath = relativePath });
            else
                excludedFiles.Add(relativePath);
        }

        foreach (var dir in includedDirs)
            EnsureDirectory(volumeId, dir.RelativePath, byPath);

        foreach (var file in includedFiles)
            EnsureDirectory(volumeId, ScanPath.Parent(file.RelativePath), byPath);

        // Directories first: the file rows need their identities.
        await _db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var file in includedFiles)
        {
            var extension = FileFilter.GetExtension(file.Name);
            _db.Files.Add(new FileEntry
            {
                VolumeId = volumeId,
                DirectoryId = byPath[ScanPath.Parent(file.RelativePath)].Id,
                Name = file.Name,
                Extension = extension,
                Category = FileFilter.ResolveCategory(extension, categoryMap),
                SizeBytes = file.SizeBytes,
                FileCreatedUtc = file.CreatedUtc,
                FileModifiedUtc = file.ModifiedUtc,
                Attributes = file.Attributes,
                IsIncluded = true,
                IsPresent = true,
                LastIndexedUtc = now,
            });
        }

        await _db.SaveChangesAsync(ct);
        await _fts.SyncVolumeFromDbAsync(volumeId, ct);

        return new IndexResult(
            includedFiles.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            excludedFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private async Task<Dictionary<string, DirectoryNode>> LoadExistingDirectoriesAsync(int volumeId, CancellationToken ct)
    {
        var existing = await _db.Directories.Where(d => d.VolumeId == volumeId).ToListAsync(ct);
        return existing.ToDictionary(d => d.MaterializedPath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Materializes <paramref name="path"/> and every missing ancestor up to the volume root
    /// (the empty path), wiring the parent navigation so EF assigns identities in the right order.
    /// </summary>
    private DirectoryNode EnsureDirectory(int volumeId, string path, Dictionary<string, DirectoryNode> byPath)
    {
        if (byPath.TryGetValue(path, out var existing))
            return existing;

        DirectoryNode? parent = null;
        if (path.Length > 0)
            parent = EnsureDirectory(volumeId, ScanPath.Parent(path), byPath);

        var node = new DirectoryNode
        {
            VolumeId = volumeId,
            Name = path.Length == 0 ? string.Empty : ScanPath.Name(path),
            MaterializedPath = path,
            Parent = parent,
            IsMaterialized = true,
        };

        _db.Directories.Add(node);
        byPath[path] = node;
        return node;
    }
}
