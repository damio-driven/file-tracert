using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;

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
    /// <para>With no items loaded the caller cannot say what landed, so the whole demand is
    /// returned. That errs in the safe direction — it can refuse a job that would have fit, never
    /// admit one that would not.</para>
    /// </summary>
    public static long OutstandingBytes(OperationJob job)
    {
        if (job.Items.Count == 0) return job.RequiredBytesTarget;

        long landed = 0;
        foreach (var item in job.Items)
            if (Array.IndexOf(Landed, item.State) >= 0)
                landed += item.SizeBytes;

        return Math.Max(0, job.RequiredBytesTarget - landed);
    }

    /// <summary>Terminal states: the job will never run again (Blocked is NOT terminal).</summary>
    public static readonly JobState[] Terminal =
    [
        JobState.Completed,
        JobState.Failed,
        JobState.Cancelled,
    ];
}
