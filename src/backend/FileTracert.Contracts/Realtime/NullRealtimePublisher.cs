namespace FileTracert.Contracts.Realtime;

/// <summary>
/// Default binding for compositions that have no transport: the hardware harness, the
/// migration-only startup path, focused unit tests. Publishing into the void is the correct
/// behaviour there — there is no client to miss the message.
/// </summary>
public sealed class NullRealtimePublisher : IRealtimePublisher
{
    public Task VolumeStatusChangedAsync(VolumeStatusChanged message, CancellationToken ct) => Task.CompletedTask;

    public Task JobProgressAsync(JobProgress message, CancellationToken ct) => Task.CompletedTask;

    public Task JobStateChangedAsync(JobStateChanged message, CancellationToken ct) => Task.CompletedTask;

    public Task ScanProgressAsync(FileTracert.Contracts.Scanning.ScanStatusDto message, CancellationToken ct) => Task.CompletedTask;

    public Task ProjectionChangedAsync(ProjectionChanged message, CancellationToken ct) => Task.CompletedTask;

    public Task NotificationRaisedAsync(NotificationRaised message, CancellationToken ct) => Task.CompletedTask;
}
