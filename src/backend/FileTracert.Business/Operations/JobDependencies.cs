using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;

namespace FileTracert.Business.Operations;

/// <summary>
/// The vocabulary of job dependencies, in one place: which block reasons mean «waiting for
/// another job», and what that implies about the projection.
///
/// §5 gives one entity at most one pending operation. The job that got there first owns the
/// entity — and therefore owns its <c>Pending*</c> overlay; whoever queues behind it is parked
/// <see cref="JobBlockReason.DependencyPending"/> and owns nothing until it is released.
/// </summary>
public static class JobDependencies
{
    /// <summary>Block reasons that mean «another job is in the way», not «the world is».</summary>
    public static readonly JobBlockReason[] Reasons =
    [
        JobBlockReason.DependencyPending,
        JobBlockReason.DependencyCancelled,
    ];

    /// <summary>True when <paramref name="job"/> is parked waiting for another job.</summary>
    public static bool IsWaitingForAnotherJob(OperationJob job) =>
        job.State == JobState.Blocked && Reasons.Contains(job.BlockReason);

    /// <summary>
    /// True when the job owns the projection overlay of the entity it operates on — i.e. every
    /// job EXCEPT one parked behind another. The single condition behind
    /// <c>OverlayWriter.ApplyAsync</c> being called or not (step 9b left it as one entry point
    /// precisely so this could be one test).
    /// </summary>
    public static bool OwnsItsEntity(OperationJob job) => !IsWaitingForAnotherJob(job);
}
