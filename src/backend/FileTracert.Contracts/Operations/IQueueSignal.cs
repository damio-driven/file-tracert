namespace FileTracert.Contracts.Operations;

/// <summary>
/// Wakes the queue processor when new work appears, so it can idle on a signal instead of
/// busy-polling the database. Signalled by the enqueue/retry paths (and, from step 10, by the
/// volume-mount and job-completion events that unblock jobs). Singleton — the API threads that
/// signal and the worker that waits live in different scopes.
/// </summary>
public interface IQueueSignal
{
    /// <summary>Raises the signal. Coalesced: many signals before the next wait count as one.</summary>
    void Signal();

    /// <summary>
    /// Waits for the next signal, returning early after <paramref name="safetyTimeout"/> so a missed
    /// wake source (e.g. an event not yet wired) can never stall the queue — the timeout is the
    /// low-frequency safety poll. Returns when signalled, when the timeout elapses, or when
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    Task WaitAsync(TimeSpan safetyTimeout, CancellationToken ct);
}
