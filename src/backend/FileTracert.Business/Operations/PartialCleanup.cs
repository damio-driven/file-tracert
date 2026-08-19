using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Removes a terminal job's leftover <c>.fadit-partial</c> files from the target and forgets the
/// pointers to them. THE one place that does it (K2): the engine and the queue service each had
/// their own copy, called from six sites between them.
///
/// <para>A partial is discardable garbage the moment the job stops being runnable — a retry copies
/// from scratch — but it is garbage that occupies bytes on the very volume the queue is planning
/// against, so nothing may skip it.</para>
/// </summary>
internal static class PartialCleanup
{
    /// <summary>
    /// Recycles every partial this job still points at and clears the pointers.
    ///
    /// <para>The pointers are persisted HERE, and with <see cref="CancellationToken.None"/>, which
    /// is the difference between the two copies that got unified: the engine's copy saved on its
    /// own with an uncancellable token, the queue service's copy left the save to the caller — so
    /// a cancel or a retry whose request token tripped, or whose transaction rolled back, deleted
    /// the file and kept a <c>TempPath</c> pointing at it. The engine's semantics wins, because
    /// the delete is not undoable: once the bytes are in the recycle bin, a row still naming them
    /// is simply false, and the false row outlives the request that produced it.</para>
    ///
    /// <para>§9 — a partial that cannot be removed (locked by another process) is logged in full
    /// and KEEPS its <c>TempPath</c>, so a later pass tries again; it never fails the user's
    /// action, which has already succeeded.</para>
    /// </summary>
    public static async Task RemoveAsync(
        FileTracertDbContext db, IFileMover mover, ILogger logger, OperationJob job)
    {
        var targetGuid = job.TargetVolume?.VolumeGuid;
        if (targetGuid is null) return;

        bool anyCleared = false;
        foreach (var item in job.Items.Where(i => !string.IsNullOrEmpty(i.TempPath)))
        {
            try
            {
                // A partial that never hit the disk (copy aborted before creating it) is
                // already clean — recycling a missing path would throw and leave the pointer.
                if (mover.Exists(targetGuid, item.TempPath!))
                    mover.DeleteToRecycleBin(targetGuid, item.TempPath!);
                item.TempPath = null;
                anyCleared = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Job {Id}: could not remove orphan partial '{Path}'.",
                    job.Id, item.TempPath);
            }
        }

        if (anyCleared)
            await db.SaveChangesAsync(CancellationToken.None);
    }
}
