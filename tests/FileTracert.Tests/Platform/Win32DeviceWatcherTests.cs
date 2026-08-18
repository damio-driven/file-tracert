using FileTracert.Platform;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Platform;

/// <summary>
/// Real cfgmgr32 registration lifecycle. A physical arrival cannot be forced from a test —
/// that is the harness' job (<c>offline-unplug</c>); what is asserted here is everything
/// around it: the registration succeeds, survives a collection, and is released exactly once.
/// </summary>
[Trait("Category", "Platform")]
public class Win32DeviceWatcherTests
{
    private static Win32DeviceWatcher Create() => new(NullLogger<Win32DeviceWatcher>.Instance);

    [Fact]
    public void Start_registers_without_throwing()
    {
        using var watcher = Create();

        var start = () => watcher.Start();

        start.Should().NotThrow();
    }

    [Fact]
    public void Start_is_idempotent()
    {
        using var watcher = Create();
        watcher.Start();

        var again = () => watcher.Start();

        again.Should().NotThrow();
    }

    /// <summary>
    /// The native side holds a raw pointer to the watcher for the whole registration. If the
    /// context handle were weak (or a marshalled delegate left unrooted), a collection here
    /// would turn the next notification into a native crash instead of an exception.
    /// </summary>
    [Fact]
    public void Registration_survives_a_garbage_collection()
    {
        using var watcher = Create();
        watcher.Start();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var dispose = () => watcher.Dispose();
        dispose.Should().NotThrow();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var watcher = Create();
        watcher.Start();

        watcher.Dispose();
        var again = () => watcher.Dispose();

        again.Should().NotThrow();
    }

    [Fact]
    public void Dispose_without_Start_is_harmless()
    {
        var watcher = Create();

        var dispose = () => watcher.Dispose();

        dispose.Should().NotThrow();
    }

    [Fact]
    public void Start_after_Dispose_throws()
    {
        var watcher = Create();
        watcher.Dispose();

        var start = () => watcher.Start();

        start.Should().Throw<ObjectDisposedException>();
    }
}
