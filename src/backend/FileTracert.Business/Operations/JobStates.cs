using FileTracert.Contracts.Enums;

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

    /// <summary>Terminal states: the job will never run again (Blocked is NOT terminal).</summary>
    public static readonly JobState[] Terminal =
    [
        JobState.Completed,
        JobState.Failed,
        JobState.Cancelled,
    ];
}
