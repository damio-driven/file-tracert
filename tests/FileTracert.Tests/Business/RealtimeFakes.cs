using System.Collections.Concurrent;
using FileTracert.Contracts.Realtime;
using FileTracert.Contracts.Scanning;

namespace FileTracert.Tests.Business;

/// <summary>
/// Records every message the Business layer publishes through the port, so a test can assert
/// the payload instead of the fact that "something was sent". Thread-safe: the engine publishes
/// from the worker thread while the test reads.
/// </summary>
internal sealed class RecordingRealtimePublisher : IRealtimePublisher
{
    private readonly ConcurrentQueue<object> _messages = new();

    public IReadOnlyList<object> Messages => [.. _messages];

    public IReadOnlyList<T> Of<T>() => [.. _messages.OfType<T>()];

    public Task VolumeStatusChangedAsync(VolumeStatusChanged message, CancellationToken ct) => Record(message);

    public Task JobProgressAsync(JobProgress message, CancellationToken ct) => Record(message);

    public Task JobStateChangedAsync(JobStateChanged message, CancellationToken ct) => Record(message);

    public Task ScanProgressAsync(ScanStatusDto message, CancellationToken ct) => Record(message);

    public Task ProjectionChangedAsync(ProjectionChanged message, CancellationToken ct) => Record(message);

    public Task NotificationRaisedAsync(NotificationRaised message, CancellationToken ct) => Record(message);

    private Task Record(object message)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A transport that is broken in the worst way: every send throws. Used to prove that a hub
/// failure cannot roll a job back (§9) — the guard is in <c>RealtimeEvents</c>, and this is what
/// exercises it against the real engine.
/// </summary>
internal sealed class ThrowingRealtimePublisher : IRealtimePublisher
{
    public int Attempts;

    public Task VolumeStatusChangedAsync(VolumeStatusChanged message, CancellationToken ct) => Boom();

    public Task JobProgressAsync(JobProgress message, CancellationToken ct) => Boom();

    public Task JobStateChangedAsync(JobStateChanged message, CancellationToken ct) => Boom();

    public Task ScanProgressAsync(ScanStatusDto message, CancellationToken ct) => Boom();

    public Task ProjectionChangedAsync(ProjectionChanged message, CancellationToken ct) => Boom();

    public Task NotificationRaisedAsync(NotificationRaised message, CancellationToken ct) => Boom();

    private Task Boom()
    {
        Interlocked.Increment(ref Attempts);
        throw new InvalidOperationException("realtime transport is down");
    }
}
