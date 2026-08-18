using FileTracert.Contracts.Realtime;
using FileTracert.Contracts.Scanning;
using Microsoft.AspNetCore.SignalR;

namespace FileTracert.Host.Realtime;

/// <summary>
/// The <see cref="IRealtimePublisher"/> port implemented over <see cref="IHubContext{THub}"/>.
/// This is the only place in the solution that knows the transport is SignalR (§3).
///
/// No try/catch here on purpose: the resilience guard is <c>Business.Realtime.RealtimeEvents</c>,
/// the single gateway every emitter goes through. Catching in both would give the same failure two
/// log entries and hide, from this side, the fact that the guard is not optional.
/// </summary>
public sealed class SignalRRealtimePublisher : IRealtimePublisher
{
    private readonly IHubContext<FileTracertHub> _hub;

    public SignalRRealtimePublisher(IHubContext<FileTracertHub> hub) => _hub = hub;

    public Task VolumeStatusChangedAsync(VolumeStatusChanged message, CancellationToken ct) =>
        Send(RealtimeMethods.VolumeStatusChanged, message, ct);

    public Task JobProgressAsync(JobProgress message, CancellationToken ct) =>
        Send(RealtimeMethods.JobProgress, message, ct);

    public Task JobStateChangedAsync(JobStateChanged message, CancellationToken ct) =>
        Send(RealtimeMethods.JobStateChanged, message, ct);

    public Task ScanProgressAsync(ScanStatusDto message, CancellationToken ct) =>
        Send(RealtimeMethods.ScanProgress, message, ct);

    public Task ProjectionChangedAsync(ProjectionChanged message, CancellationToken ct) =>
        Send(RealtimeMethods.ProjectionChanged, message, ct);

    public Task NotificationRaisedAsync(NotificationRaised message, CancellationToken ct) =>
        Send(RealtimeMethods.NotificationRaised, message, ct);

    private Task Send(string method, object payload, CancellationToken ct) =>
        _hub.Clients.All.SendAsync(method, payload, ct);
}
