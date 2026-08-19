namespace FileTracert.Host.Logging;

/// <summary>
/// Drains the log queue when the host stops.
/// <para>
/// The processor is a pre-built instance (the sink has to exist before anything can log,
/// which is well before the container does), and the DI container never disposes what it did
/// not create — so nothing was calling its drain and up to ~10 000 queued records died with
/// the process on every stop. Exactly the shutdown records, which are the ones worth having.
/// </para>
/// <para>
/// It is a hosted service, and registered <em>first</em>, because hosted services stop in
/// reverse registration order: stopping last means every worker has already written its
/// goodbye by the time the queue is closed. The wait is capped inside
/// <see cref="SqliteLogProcessor.DrainAsync"/> — a stop must never become a hang — and the
/// host's own shutdown token caps it a second time.
/// </para>
/// </summary>
public sealed class LogFlushService : IHostedService
{
    private readonly SqliteLogProcessor _processor;

    public LogFlushService(SqliteLogProcessor processor)
    {
        _processor = processor;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Deliberately says nothing through <c>ILogger</c>: this runs while the logging pipeline is
    /// being torn down, and a provider that is already disposed turns a log call into an
    /// exception that fails the whole stop (seen with the Windows EventLog provider). What the
    /// drain has to report, it reports outside the pipeline — see
    /// <see cref="SqliteLogProcessor.DrainAsync"/>.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => _processor.DrainAsync(cancellationToken);
}
