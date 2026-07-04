using System.Threading.Channels;
using FileTracert.Contracts.Operations;

namespace FileTracert.Business.Operations;

/// <summary>
/// <see cref="IQueueSignal"/> backed by a capacity-1 <see cref="Channel{T}"/>. Multiple
/// <see cref="Signal"/> calls before a wait coalesce into a single wake (DropWrite on a full
/// channel), which is exactly the desired semantics: "there is work, look now".
/// </summary>
public sealed class QueueSignal : IQueueSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public void Signal() => _channel.Writer.TryWrite(0);

    public async Task WaitAsync(TimeSpan safetyTimeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(safetyTimeout);
        try
        {
            await _channel.Reader.ReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Safety-poll timeout, not a real cancellation: fall through so the worker re-checks.
        }
    }
}
