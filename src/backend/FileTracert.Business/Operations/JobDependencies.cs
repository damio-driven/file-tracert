using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// Re-parks everything waiting for <paramref name="prerequisite"/> now that it has ended
    /// badly (cancelled by the user, or failed). §5 is explicit: NEVER a cascade of
    /// cancellations — the dependents stay in the queue on a reason that says what happened,
    /// reactivatable with «Riprova».
    ///
    /// Must be called inside the transaction that commits the prerequisite's terminal state, so
    /// a crash can never leave dependents pointing at a job that is already gone.
    ///
    /// A conditional UPDATE (<c>WHERE State = Blocked</c>) rather than tracked entities: it
    /// touches no <c>State</c>, so it cannot race the concurrency token of a dependent that
    /// someone else is moving, and one statement covers however many are waiting.
    /// </summary>
    /// <returns>How many dependents were re-parked.</returns>
    public static async Task<int> ParkDependentsAsync(
        FileTracertDbContext db, OperationJob prerequisite, CancellationToken ct)
    {
        var message =
            $"L'operazione #{prerequisite.Id} ({prerequisite.Type}) da cui dipendeva è terminata " +
            $"come {prerequisite.State}: l'operazione resta in coda, verificare e riprovare.";

        return await db.OperationJobs
            .Where(j => j.DependsOnJobId == prerequisite.Id && j.State == JobState.Blocked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.BlockReason, JobBlockReason.DependencyCancelled)
                .SetProperty(j => j.ErrorMessage, message)
                .SetProperty(j => j.UpdatedUtc, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Why <paramref name="job"/> must not run yet, or null when its prerequisite is satisfied.
    /// The last line of defence (§2.3): <c>Blocked</c> already keeps a dependent out of the
    /// processor's query, but a manual «Riprova» or a revaluation gone wrong could put one back
    /// in front of its prerequisite, and a job executed out of order corrupts real files.
    /// </summary>
    public static async Task<(JobBlockReason Reason, string Message)?> BarrierAsync(
        FileTracertDbContext db, OperationJob job, CancellationToken ct)
    {
        if (job.DependsOnJobId is not { } prerequisiteId) return null;

        var prerequisite = await db.OperationJobs.AsNoTracking()
            .Where(j => j.Id == prerequisiteId)
            .Select(j => new { j.Id, j.State, j.Type })
            .FirstOrDefaultAsync(ct);

        // No row left to wait for: nothing can un-block this job, so let it through rather than
        // deadlock it forever on a prerequisite that no longer exists.
        if (prerequisite is null || prerequisite.State == JobState.Completed) return null;

        return prerequisite.State is JobState.Cancelled or JobState.Failed
            ? (JobBlockReason.DependencyCancelled,
               $"L'operazione #{prerequisite.Id} ({prerequisite.Type}) da cui dipende è terminata " +
               $"come {prerequisite.State}: verificare e riprovare.")
            : (JobBlockReason.DependencyPending,
               $"In attesa dell'operazione #{prerequisite.Id} ({prerequisite.Type}), " +
               $"attualmente {prerequisite.State}.");
    }
}
