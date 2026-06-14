using System.Threading.Channels;
using FileTracert.Contracts.Logging;

namespace FileTracert.Host.Logging;

/// <summary>
/// Background pump between the <see cref="SqliteLogger"/> and the <see cref="ILogStore"/>:
/// a bounded channel drained by a single consumer that writes in batches. Enqueue is
/// non-blocking and never throws (overflow drops the newest record rather than
/// stalling the app); a sink failure is swallowed so logging can never crash the
/// process. Disposing completes the channel and flushes what is queued.
/// </summary>
public sealed class SqliteLogProcessor : IAsyncDisposable
{
    private readonly ILogStore _store;
    private readonly Channel<LogRecord> _channel;
    private readonly Task _consumer;
    private readonly int _batchSize;

    public SqliteLogProcessor(ILogStore store, int capacity = 10_000, int batchSize = 200)
    {
        _store = store;
        _batchSize = batchSize;
        _channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(capacity)
        {
            // Logging must never block the application; under a flood we drop.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>Queues a record for persistence. Non-blocking; never throws.</summary>
    public void Enqueue(LogRecord record) => _channel.Writer.TryWrite(record);

    private async Task ConsumeAsync()
    {
        var reader = _channel.Reader;
        var buffer = new List<LogRecord>(_batchSize);

        try
        {
            while (await reader.WaitToReadAsync())
            {
                buffer.Clear();
                while (buffer.Count < _batchSize && reader.TryRead(out var record))
                {
                    buffer.Add(record);
                }

                if (buffer.Count == 0)
                {
                    continue;
                }

                try
                {
                    await _store.WriteBatchAsync(buffer, CancellationToken.None);
                }
                catch
                {
                    // A failure to persist logs must never take down the application.
                }
            }
        }
        catch
        {
            // Defensive: the consumer loop itself must never surface an exception.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _consumer;
        }
        catch
        {
            // best-effort flush on shutdown
        }
    }
}
