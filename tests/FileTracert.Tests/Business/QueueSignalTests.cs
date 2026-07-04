using System.Diagnostics;
using FileTracert.Business.Operations;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// C6: the processor idles on <see cref="QueueSignal"/> instead of a fixed 3 s poll. A signal must
/// wake a waiter promptly (no busy-poll latency); with no signal the wait returns only after the
/// safety timeout; and bursts of signals coalesce.
/// </summary>
public sealed class QueueSignalTests
{
    [Fact]
    public async Task Signal_before_wait_returns_immediately()
    {
        var signal = new QueueSignal();
        signal.Signal();

        var sw = Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        sw.Stop();

        // A pre-existing signal is consumed at once — nowhere near the safety timeout.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Signal_raised_during_wait_wakes_the_waiter()
    {
        var signal = new QueueSignal();

        var waiter = signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        waiter.IsCompleted.Should().BeFalse("nothing signalled yet");

        signal.Signal();

        // Completes on the signal, long before the 30 s safety poll.
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        waiter.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Wait_returns_after_safety_timeout_when_no_signal()
    {
        var signal = new QueueSignal();

        var sw = Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);
        sw.Stop();

        // No signal → the safety poll fires and the wait returns (does not hang, does not throw).
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Multiple_signals_coalesce_into_one_wake()
    {
        var signal = new QueueSignal();
        signal.Signal();
        signal.Signal();
        signal.Signal();

        // First wait consumes the (single, coalesced) signal immediately.
        await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // Second wait has nothing left → falls through on the short safety timeout.
        var sw = Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);
        sw.Stop();
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Wait_honours_external_cancellation()
    {
        var signal = new QueueSignal();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A real cancellation (shutdown) propagates — only the safety-timeout cancellation is swallowed.
        var act = async () => await signal.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
