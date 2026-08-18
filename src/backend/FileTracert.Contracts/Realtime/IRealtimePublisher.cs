using FileTracert.Contracts.Scanning;

namespace FileTracert.Contracts.Realtime;

/// <summary>
/// Port towards the real-time transport (§3). SignalR is a <c>Host</c> dependency, so
/// <c>Business</c> — which may not reference it — talks to this instead; <c>Host</c> supplies the
/// <c>IHubContext</c>-backed implementation.
///
/// Every method is <strong>best-effort</strong>: a transport failure is never allowed to fail the
/// operation that produced the event (§9 — resilience, with a full log, not silence). Callers in
/// <c>Business</c> go through the single guarded wrapper instead of catching one by one.
/// </summary>
public interface IRealtimePublisher
{
    Task VolumeStatusChangedAsync(VolumeStatusChanged message, CancellationToken ct);

    Task JobProgressAsync(JobProgress message, CancellationToken ct);

    Task JobStateChangedAsync(JobStateChanged message, CancellationToken ct);

    /// <summary>Reuses the polled shape (<see cref="ScanStatusDto"/>) so push and poll agree.</summary>
    Task ScanProgressAsync(ScanStatusDto message, CancellationToken ct);

    Task ProjectionChangedAsync(ProjectionChanged message, CancellationToken ct);

    Task NotificationRaisedAsync(NotificationRaised message, CancellationToken ct);
}
