using System.Text.Json;
using FileTracert.Business.Filtering;
using FileTracert.Contracts.Dtos;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Setup;

/// <summary>Raised when a create/patch would violate the nesting or duplicate policy.</summary>
public sealed class WatchedRootConflictException(string reason) : Exception(reason);

/// <summary>
/// Transactional CRUD for monitored roots. All writes go through <c>Load+Update</c>
/// (no double reads). Filter changes reconcile the existing index in place.
/// </summary>
public sealed class WatchedRootsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly FileTracertDbContext _db;
    private readonly FilterReconciler _reconciler;

    public WatchedRootsService(FileTracertDbContext db, FilterReconciler reconciler)
    {
        _db = db;
        _reconciler = reconciler;
    }

    public async Task<WatchedRootDto> CreateAsync(int volumeId, CreateWatchedRootRequest request, CancellationToken ct)
    {
        var volume = await _db.Volumes.FirstOrDefaultAsync(v => v.Id == volumeId, ct)
            ?? throw new KeyNotFoundException($"Volume {volumeId} not found.");

        if (!WatchedRootPath.TryValidate(request.RelativePath, out var normalized, out var error))
        {
            throw new InvalidPathException(error);
        }

        var existing = await _db.WatchedRoots.Where(r => r.VolumeId == volumeId).Select(r => r.RelativePath).ToListAsync(ct);
        if (existing.Any(e => WatchedRootPath.Conflicts(e, normalized)))
        {
            throw new WatchedRootConflictException(
                "Esiste già una cartella monitorata uguale, contenuta o che contiene questa.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var root = new WatchedRoot
        {
            VolumeId = volumeId,
            RelativePath = normalized,
            IsActive = true,
            FilterOverrideJson = SerializeOverride(request.FilterOverride),
        };
        _db.WatchedRoots.Add(root);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
        return await ToDtoAsync(volume, root, ct);
    }

    public async Task<(WatchedRootDto Dto, ReconcileResultDto? Reconcile)> UpdateAsync(
        int rootId, UpdateWatchedRootRequest request, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var root = await _db.WatchedRoots.Include(r => r.Volume).FirstOrDefaultAsync(r => r.Id == rootId, ct)
            ?? throw new KeyNotFoundException($"Watched root {rootId} not found.");

        if (request.IsActive is { } active)
        {
            root.IsActive = active;
        }

        ReconcileResultDto? reconcile = null;
        if (request.FilterOverride is { } overrideDto)
        {
            var settings = await _db.AppSettings.FirstAsync(ct);
            var oldFilter = EffectiveFilterBuilder.Build(settings, root.FilterOverrideJson);

            root.FilterOverrideJson = SerializeOverride(overrideDto);
            var newFilter = EffectiveFilterBuilder.Build(settings, root.FilterOverrideJson);

            var (inc, exc) = await _reconciler.ReconcileRootAsync(root, newFilter, ct);
            reconcile = new ReconcileResultDto(inc, exc, FilterReconciler.FilterWidened(oldFilter, newFilter));
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return (await ToDtoAsync(root.Volume, root, ct), reconcile);
    }

    public async Task DeleteAsync(int rootId, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var root = await _db.WatchedRoots.FirstOrDefaultAsync(r => r.Id == rootId, ct)
            ?? throw new KeyNotFoundException($"Watched root {rootId} not found.");

        await _reconciler.ExcludeAllUnderAsync(root, ct);
        _db.WatchedRoots.Remove(root);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
    }

    /// <summary>Serializes a per-root override; <c>UseDefault</c> (or null) → no override stored.</summary>
    private static string? SerializeOverride(FilterOverrideDto? dto)
    {
        if (dto is null || dto.UseDefault)
        {
            return null;
        }

        var extensions = EffectiveFilterBuilder.NormalizeExtensions(dto.Extensions);
        return JsonSerializer.Serialize(new FilterOverride { Extensions = extensions }, JsonOptions);
    }

    private async Task<WatchedRootDto> ToDtoAsync(Volume volume, WatchedRoot root, CancellationToken ct)
    {
        var settings = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppSettings();
        var extensionToCategory = await _db.ExtensionCategories.AsNoTracking()
            .ToDictionaryAsync(e => e.Extension, e => e.Category, ct);

        var summary = EffectiveFilterDescriber.Describe(
            EffectiveFilterBuilder.Build(settings, root.FilterOverrideJson), extensionToCategory);

        return new WatchedRootDto(root.Id, root.RelativePath, root.IsActive, summary);
    }
}
