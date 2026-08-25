using FileTracert.Data.Cancellation;

namespace FileTracert.Host.Infrastructure;

/// <summary>
/// Fires <see cref="DatabaseShutdownSource"/> from the stop sequence, at the one moment that is
/// right (14b).
///
/// <para>Registration order is the mechanism, so it is worth naming: hosted services stop in
/// REVERSE registration order. <c>GenericWebHostService</c> is registered by
/// <c>WebApplication.CreateBuilder</c> before any line of ours, so Kestrel drains LAST;
/// <c>LogFlushService</c> is registered second and drains the log queue second-to-last (step 11c);
/// this is third, so it runs after every worker has already been stopped and before the request
/// drain begins.</para>
///
/// <para>That gap is exactly the wait step 13 measured: the workers are gone, and what is left
/// holding the service is the in-flight HTTP reads. Firing at <c>ApplicationStopping</c> instead
/// would land while the workers still run and turn their clean stop into logged errors — see
/// <see cref="DatabaseShutdownSource"/>.</para>
/// </summary>
public sealed class ReadCancellationLifetime : IHostedService
{
    private readonly DatabaseShutdownSource _source;
    private readonly ILogger<ReadCancellationLifetime> _logger;

    public ReadCancellationLifetime(DatabaseShutdownSource source, ILogger<ReadCancellationLifetime> logger)
    {
        _source = source;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Stopping: in-flight database reads will be interrupted.");
        _source.Stop();
        return Task.CompletedTask;
    }
}
