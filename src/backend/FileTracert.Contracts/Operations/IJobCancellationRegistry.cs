namespace FileTracert.Contracts.Operations;

/// <summary>
/// Singleton bridge between the API and the queue processor. A Cancel request runs on an
/// API-scoped DbContext and writes <c>State=Cancelled</c>, but the job is executing on a
/// SEPARATE DbContext inside the worker — invisible to it. This registry lets Cancel signal
/// the <see cref="CancellationToken"/> of the in-flight job so an ongoing copy stops instead
/// of running to completion and trashing the source (the project's most sacred invariant).
/// </summary>
public interface IJobCancellationRegistry
{
    /// <summary>
    /// Registers a per-job <see cref="CancellationTokenSource"/> linked to
    /// <paramref name="linkedTo"/> (the worker's stopping token) and returns the token the
    /// job must execute under. Call once before executing a job.
    /// </summary>
    CancellationToken Register(int jobId, CancellationToken linkedTo);

    /// <summary>Signals cancellation for a currently-executing job. No-op if not running.</summary>
    void Cancel(int jobId);

    /// <summary>Unregisters and disposes the job's source. Call in a finally after execution.</summary>
    void Remove(int jobId);
}
