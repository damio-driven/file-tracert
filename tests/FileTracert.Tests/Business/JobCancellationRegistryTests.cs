using FileTracert.Business.Operations;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Unit tests for <see cref="JobCancellationRegistry"/> — the singleton that lets the API's
/// Cancel signal the token a job is executing under (across two DbContexts).
/// </summary>
public sealed class JobCancellationRegistryTests
{
    [Fact]
    public void Cancel_signals_the_registered_token()
    {
        var registry = new JobCancellationRegistry();
        var token = registry.Register(jobId: 7, CancellationToken.None);

        token.IsCancellationRequested.Should().BeFalse();

        registry.Cancel(7);

        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Cancel_for_unknown_job_is_a_noop()
    {
        var registry = new JobCancellationRegistry();

        var act = () => registry.Cancel(999);

        act.Should().NotThrow();
    }

    [Fact]
    public void Remove_stops_a_later_Cancel_from_signalling_a_reused_token()
    {
        var registry = new JobCancellationRegistry();
        var token = registry.Register(jobId: 3, CancellationToken.None);
        registry.Remove(3);

        registry.Cancel(3); // no live source anymore

        token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void Register_links_to_the_parent_token()
    {
        var registry = new JobCancellationRegistry();
        using var parent = new CancellationTokenSource();
        var token = registry.Register(jobId: 1, parent.Token);

        parent.Cancel();

        token.IsCancellationRequested.Should().BeTrue();
    }
}
