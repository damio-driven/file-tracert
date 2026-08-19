using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Scanning;
using FileTracert.Data.Entities;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Projection;

/// <summary>
/// Finds — or creates — the <see cref="DirectoryNode"/> row for a volume-relative path,
/// walking up and materializing every missing ancestor on the way.
///
/// Single home for that walk. Two callers need it and they need it with opposite meanings:
/// the post-execution index update knows the folder is physically there, while the enqueue
/// only knows it is going to be there. Keeping one class with two entry points is what stops
/// the two from drifting on the flags (which is how a "ghost" directory is born).
/// </summary>
public sealed class DirectoryResolver
{
    private readonly FileTracertDbContext _db;

    public DirectoryResolver(FileTracertDbContext db) => _db = db;

    /// <summary>
    /// Physical resolution: the folder exists on disk. An existing row is promoted to
    /// materialized + present so a row previously created as a mere projection placeholder
    /// (or marked absent by a scan) catches up with the physical fact instead of a second
    /// row being created next to it.
    /// </summary>
    public Task<DirectoryNode> FindOrCreateMaterializedAsync(int volumeId, string path, CancellationToken ct) =>
        ResolveAsync(volumeId, path, pendingJobId: null, ct);

    /// <summary>
    /// Projection resolution (§5): the folder does not exist yet — <paramref name="pendingJobId"/>
    /// is the job that will create it. Missing rows are inserted with
    /// <c>IsMaterialized = false</c>, <c>IsPresent = false</c> and a
    /// <see cref="EntityPendingState.PendingCreate"/> overlay, so the Catalog shows the
    /// destination immediately and an operation can target it before it exists.
    /// A folder that already exists physically gets NO overlay: there is nothing pending on it.
    ///
    /// DEVIATION from the step 9b plan, deliberate: the plan had a move's implicitly created
    /// target directory carry NO overlay. Such a row is neither materialized+present nor
    /// pending, so the Catalog's visibility rule hides it — and a file moved into a folder the
    /// picker invented inline would sit in a folder nothing can show, breaking acceptance
    /// criterion 1. The row IS being created by this job, so it says so; it is then promoted on
    /// completion and cleared on cancel by exactly the same code as an explicit CreateFolder.
    /// </summary>
    public Task<DirectoryNode> FindOrCreateProjectedAsync(
        int volumeId, string path, int pendingJobId, CancellationToken ct) =>
        ResolveAsync(volumeId, path, pendingJobId, ct);

    private async Task<DirectoryNode> ResolveAsync(
        int volumeId, string path, int? pendingJobId, CancellationToken ct)
    {
        var existing = await _db.Directories
            .FirstOrDefaultAsync(d => d.VolumeId == volumeId && d.MaterializedPath == path, ct);

        if (existing is not null)
        {
            if (ApplyToExisting(existing, path, pendingJobId))
                await _db.SaveChangesAsync(ct);
            return existing;
        }

        DirectoryNode? parent = null;
        if (!string.IsNullOrEmpty(path))
            parent = await ResolveAsync(volumeId, ScanPath.Parent(path), pendingJobId, ct);

        // The volume root always exists physically, whoever asked for it: never stamp it
        // PendingCreate, or a cancelled job would make the whole volume disappear from the tree.
        var exists = pendingJobId is null || IsVolumeRoot(path);

        var node = new DirectoryNode
        {
            VolumeId = volumeId,
            ParentId = parent?.Id,
            Name = IsVolumeRoot(path) ? string.Empty : ScanPath.Name(path),
            MaterializedPath = path,
            IsMaterialized = exists,
            IsPresent = exists,
            PendingState = exists ? EntityPendingState.None : EntityPendingState.PendingCreate,
            PendingJobId = exists ? null : pendingJobId,
        };

        _db.Directories.Add(node);
        await _db.SaveChangesAsync(ct);
        return node;
    }

    /// <summary>Returns true when the row was mutated and needs a save.</summary>
    private static bool ApplyToExisting(DirectoryNode existing, string path, int? pendingJobId)
    {
        if (pendingJobId is null)
        {
            // Physical resolution: catch the row up with what is now on disk.
            if (existing.IsMaterialized && existing.IsPresent) return false;
            existing.IsMaterialized = true;
            existing.IsPresent = true;
            return true;
        }

        // Projection resolution: only a row that does NOT stand for something on disk, and that
        // no other job already owns, becomes this job's pending creation. Everything else is left
        // exactly as it is — an enqueue must never steal another job's overlay.
        if (IsVolumeRoot(path)) return false;
        if (existing.IsMaterialized && existing.IsPresent) return false;
        if (existing.PendingState != EntityPendingState.None) return false;

        existing.PendingState = EntityPendingState.PendingCreate;
        existing.PendingJobId = pendingJobId;
        return true;
    }

    private static bool IsVolumeRoot(string path) => string.IsNullOrEmpty(path);
}
