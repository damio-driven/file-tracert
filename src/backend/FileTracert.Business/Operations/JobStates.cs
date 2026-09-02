using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Business.Operations;

/// <summary>Shared job-state groupings used by the queue processor and its queries.</summary>
public static class JobStates
{
    /// <summary>
    /// States the processor should pick up: ready to run or already in-flight from a prior run
    /// (resumed on restart). Kept as an array so <c>Contains</c> translates to a SQL <c>IN</c>.
    /// </summary>
    public static readonly JobState[] Runnable =
    [
        JobState.Pending,
        JobState.SpaceReserved,
        JobState.Copying,
        JobState.Verifying,
        JobState.DeletingSource,
    ];

    /// <summary>
    /// States in which the engine is physically working on the job right now. Pending and
    /// SpaceReserved are queued, not running: nothing is being copied for them yet.
    ///
    /// These three also appear in <see cref="Runnable"/>, which answers a different question
    /// ("should the processor pick this up?", resumed in-flight states included). Kept as a
    /// separate array rather than derived from it because both must translate to a SQL <c>IN</c>.
    /// </summary>
    public static readonly JobState[] Active =
    [
        JobState.Copying,
        JobState.Verifying,
        JobState.DeletingSource,
    ];

    /// <summary>
    /// Item states whose bytes are ALREADY on the target volume. <c>Copied</c> means the payload
    /// is written (still under its <c>.fadit-partial</c> name), <c>Verified</c> that it has been
    /// finalized, <c>Done</c> that the source has been dealt with too. An item in any other state
    /// has written nothing that survives: <c>Copying</c> is reset to <c>Pending</c> and re-copied
    /// from scratch on resume, and its partial is truncated by <c>FileMode.Create</c>.
    /// </summary>
    public static readonly JobItemState[] Landed =
    [
        JobItemState.Copied,
        JobItemState.Verified,
        JobItemState.Done,
    ];

    /// <summary>
    /// How many bytes of this job's demand are still to be WRITTEN to the target.
    ///
    /// <para>THE single answer, and it has to be single: the engine asks it before resuming a copy
    /// and the revaluation asks it before releasing a parked job, and the revaluation's own comment
    /// says what happens when those two judge on different numbers — the job ping-pongs
    /// <c>Blocked → Pending → Blocked</c> without ever moving a byte.</para>
    ///
    /// <para>It exists because a resumed job has already put part of its demand on the target.
    /// Re-asking the drive for the WHOLE of it would double-count what is sitting there and park
    /// every interrupted large job for ever: 9 GB of a 10 GB copy written, half a gigabyte free,
    /// and a job that needs 1 GB more refused for wanting 10.</para>
    ///
    /// <para>With no items loaded and nothing passed in, the caller cannot say what landed, so
    /// the whole demand is returned. That errs in the safe direction for a DECIDER — it can refuse
    /// a job that would have fit, never admit one that would not — but it is the wrong number to
    /// SHOW, which is why <paramref name="landedBytes"/> exists.</para>
    /// </summary>
    /// <param name="landedBytes">
    /// What the caller already knows is on the target, when it knows it from somewhere other than
    /// the loaded items. Exactly one caller needs this: the queue LIST, which must not materialise
    /// every item of every job on the page (step 11e, E1) and so reads the figure as a grouped sum
    /// instead. Everything that DECIDES loads the items and leaves this null — the derivation stays
    /// in one place either way, and no decider can pass the wrong thing because it passes nothing.
    /// </param>
    public static long OutstandingBytes(OperationJob job, long? landedBytes = null)
    {
        long landed = landedBytes ?? LandedFromItems(job);
        return Math.Max(0, job.RequiredBytesTarget - landed);
    }

    /// <summary>Bytes of this job already on the target, read off the loaded items. Zero when none are loaded.</summary>
    private static long LandedFromItems(OperationJob job)
    {
        long landed = 0;
        foreach (var item in job.Items)
            if (Array.IndexOf(Landed, item.State) >= 0)
                landed += item.SizeBytes;
        return landed;
    }

    /// <summary>
    /// The landed bytes of several jobs at once, without materialising a single
    /// <see cref="OperationJobItem"/>. For the queue list (E1): a page holding a cross-volume
    /// folder job would otherwise load one entity per file to print one number.
    /// </summary>
    public static async Task<Dictionary<int, long>> LandedBytesAsync(
        FileTracertDbContext db, IReadOnlyList<int> jobIds, CancellationToken ct)
    {
        if (jobIds.Count == 0) return [];

        var rows = await db.OperationJobItems.AsNoTracking()
            .Where(i => jobIds.Contains(i.JobId) && Landed.Contains(i.State))
            .GroupBy(i => i.JobId)
            .Select(g => new { JobId = g.Key, Bytes = g.Sum(i => i.SizeBytes) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.JobId, r => r.Bytes);
    }

    /// <summary>Terminal states: the job will never run again (Blocked is NOT terminal).</summary>
    public static readonly JobState[] Terminal =
    [
        JobState.Completed,
        JobState.Failed,
        JobState.Cancelled,
    ];
}
