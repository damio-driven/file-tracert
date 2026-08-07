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

    /// <summary>Terminal states: the job will never run again (Blocked is NOT terminal).</summary>
    public static readonly JobState[] Terminal =
    [
        JobState.Completed,
        JobState.Failed,
        JobState.Cancelled,
    ];
}
