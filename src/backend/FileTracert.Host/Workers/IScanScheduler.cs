using System.Threading.Channels;

namespace FileTracert.Host.Workers;

/// <summary>
/// In-process trigger so other components can ask for a volume to be scanned
/// without waiting for the periodic poll. MVP only — replaced/extended when
/// mount events and the real scan queue land in later steps.
/// </summary>
public interface IScanScheduler
{
    /// <summary>Request a full scan of the given volume id.</summary>
    void RequestScan(int volumeId);

    /// <summary>Stream of requested volume ids, consumed by <see cref="ScanWorker"/>.</summary>
    ChannelReader<int> Requests { get; }
}

/// <summary>Singleton backed by an unbounded channel.</summary>
public sealed class ScanScheduler : IScanScheduler
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<int> Requests => _channel.Reader;

    public void RequestScan(int volumeId) => _channel.Writer.TryWrite(volumeId);
}
