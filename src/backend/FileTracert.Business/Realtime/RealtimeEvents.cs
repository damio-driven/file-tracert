using FileTracert.Contracts.Realtime;
using FileTracert.Contracts.Scanning;
using FileTracert.Data.Entities;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Realtime;

/// <summary>
/// The one guarded gateway between the Business layer and the real-time transport.
///
/// Two reasons it exists instead of injecting <see cref="IRealtimePublisher"/> everywhere:
/// <list type="number">
/// <item>a broken hub must never break a job (§9) — the catch, and the full-exception log that
/// makes it resilience rather than silence, live here <em>once</em> instead of at a dozen call
/// sites;</item>
/// <item>the payloads are built from the domain entities in a single place, so an event has one
/// shape wherever it is raised.</item>
/// </list>
///
/// Every method is fire-safe: it returns a completed task and never throws. Publishing always
/// uses <see cref="CancellationToken.None"/> on purpose — the caller's token belongs to the work
/// that produced the fact, and that fact is already committed by the time we get here; a cancelled
/// request must not silently swallow the notification of a change it already made.
/// </summary>
public sealed class RealtimeEvents
{
    private readonly IRealtimePublisher _publisher;
    private readonly ILogger<RealtimeEvents> _logger;

    public RealtimeEvents(IRealtimePublisher publisher, ILogger<RealtimeEvents> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    public Task VolumeStatusChangedAsync(int volumeId, bool isOnline, long freeBytesLastKnown, DateTime lastSeenUtc) =>
        SafeAsync(
            RealtimeMethods.VolumeStatusChanged,
            () => _publisher.VolumeStatusChangedAsync(
                new VolumeStatusChanged(volumeId, isOnline, freeBytesLastKnown, lastSeenUtc),
                CancellationToken.None));

    public Task JobProgressAsync(OperationJob job) =>
        SafeAsync(
            RealtimeMethods.JobProgress,
            () => _publisher.JobProgressAsync(
                new JobProgress(job.Id, job.BytesProcessed, job.TotalBytes), CancellationToken.None));

    public Task JobStateChangedAsync(OperationJob job) =>
        SafeAsync(
            RealtimeMethods.JobStateChanged,
            () => _publisher.JobStateChangedAsync(
                new JobStateChanged(job.Id, job.State, job.BlockReason, job.ErrorMessage),
                CancellationToken.None));

    public Task ScanProgressAsync(ScanStatusDto status) =>
        SafeAsync(
            RealtimeMethods.ScanProgress,
            () => _publisher.ScanProgressAsync(status, CancellationToken.None));

    /// <summary>
    /// The overlay of <paramref name="job"/> changed. The volume is reported only when the job
    /// lives on exactly one — a cross-volume move moved the entity between two, and naming either
    /// would let a client refresh the wrong half.
    /// </summary>
    public Task ProjectionChangedAsync(OperationJob job) =>
        SafeAsync(
            RealtimeMethods.ProjectionChanged,
            () => _publisher.ProjectionChangedAsync(
                new ProjectionChanged(SingleVolumeOf(job), job.Id), CancellationToken.None));

    /// <summary>
    /// The catalog of one volume moved with no job behind it — the incremental USN pass found work
    /// done outside the application. Same message as an overlay change because it asks the client
    /// for the same thing (re-read Catalogo/Ricerca for this volume), and it carries no job id
    /// because there is no job: nobody queued this, the disk simply changed.
    /// </summary>
    public Task CatalogChangedAsync(int volumeId) =>
        SafeAsync(
            RealtimeMethods.ProjectionChanged,
            () => _publisher.ProjectionChangedAsync(
                new ProjectionChanged(volumeId, JobId: null), CancellationToken.None));

    public Task NotificationRaisedAsync(Notification notification) =>
        SafeAsync(
            RealtimeMethods.NotificationRaised,
            () => _publisher.NotificationRaisedAsync(
                new NotificationRaised(
                    notification.Id, notification.Severity, notification.Title, notification.TimestampUtc),
                CancellationToken.None));

    private static int? SingleVolumeOf(OperationJob job)
    {
        var source = job.SourceVolumeId;
        var target = job.TargetVolumeId;
        if (source is null) return target;
        if (target is null) return source;
        return source == target ? source : null;
    }

    private async Task SafeAsync(string message, Func<Task> publish)
    {
        try
        {
            await publish();
        }
        catch (OperationCanceledException ex)
        {
            // The transport was torn down mid-send, in practice at shutdown. Nothing is wrong and
            // nobody is left waiting for this message, so it is recorded without raising an Error.
            _logger.LogDebug(ex, "Realtime publish of {Message} was cancelled (transport closing).", message);
        }
        catch (Exception ex)
        {
            // Resilience, not silence (§9): the fact is already persisted, so the operation must
            // go on — but the failure is logged in full. No Notification: a transport hiccup fixes
            // itself at the next reconnect, and the bell is for problems the user can act on.
            _logger.LogError(ex, "Realtime publish of {Message} failed — ignored (best-effort).", message);
        }
    }
}
