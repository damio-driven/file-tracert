using FileTracert.Contracts.Platform;

namespace FileTracert.Tests.Host;

/// <summary>
/// Stands in for the OS notification source. The component under test here is the worker —
/// the real cfgmgr32 registration has its own tests in <c>Platform/Win32DeviceWatcherTests</c>.
/// </summary>
internal sealed class FakeDeviceWatcher : IDeviceWatcher
{
    private int _startCount;
    private int _disposeCount;

    public event EventHandler<DeviceChangeEvent>? Changed;

    /// <summary>Set to make <see cref="Start"/> fail the way a refused registration would.</summary>
    public Exception? StartFailure { get; set; }

    public int StartCount => Volatile.Read(ref _startCount);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>True once the worker has subscribed — raising before that would be lost.</summary>
    public bool HasSubscribers => Changed is not null;

    public void Start()
    {
        Interlocked.Increment(ref _startCount);
        if (StartFailure is not null)
        {
            throw StartFailure;
        }
    }

    public void Raise(DeviceChangeKind kind = DeviceChangeKind.Arrived) =>
        Changed?.Invoke(this, new DeviceChangeEvent(kind, DateTime.UtcNow));

    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}

/// <summary>Counts how many sync cycles actually reached the platform.</summary>
internal sealed class CountingVolumesProbe(IReadOnlyList<ProbedVolume> volumes) : IVolumeProbe
{
    private int _enumerations;

    public int Enumerations => Volatile.Read(ref _enumerations);

    public IReadOnlyList<ProbedVolume> EnumerateVolumes()
    {
        Interlocked.Increment(ref _enumerations);
        return volumes;
    }

    public ProbedVolume? TryGetByGuid(string volumeGuid) =>
        volumes.FirstOrDefault(v => string.Equals(v.VolumeGuid, volumeGuid, StringComparison.OrdinalIgnoreCase));

    public long? TryGetFreeBytes(string volumeGuid) => TryGetByGuid(volumeGuid)?.FreeBytes;
}

/// <summary>
/// Holds a sync cycle inside the platform call until released, and records how many cycles were
/// ever in there at once — the only way to observe the gate that serializes the two triggers.
/// </summary>
internal sealed class LatchingVolumesProbe : IVolumeProbe
{
    private readonly SemaphoreSlim _entered = new(0);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inside;
    private int _maxInside;
    private int _enumerations;

    public int MaxConcurrent => Volatile.Read(ref _maxInside);

    public int Enumerations => Volatile.Read(ref _enumerations);

    /// <summary>Completes when a cycle has entered the probe.</summary>
    public Task EnteredAsync() => _entered.WaitAsync(TimeSpan.FromSeconds(10));

    public void Release() => _release.TrySetResult();

    public IReadOnlyList<ProbedVolume> EnumerateVolumes()
    {
        Interlocked.Increment(ref _enumerations);
        int inside = Interlocked.Increment(ref _inside);

        int seen = Volatile.Read(ref _maxInside);
        while (inside > seen)
        {
            int previous = Interlocked.CompareExchange(ref _maxInside, inside, seen);
            if (previous == seen)
            {
                break;
            }

            seen = previous;
        }

        _entered.Release();
        _release.Task.GetAwaiter().GetResult();
        Interlocked.Decrement(ref _inside);
        return [];
    }

    public ProbedVolume? TryGetByGuid(string volumeGuid) => null;

    public long? TryGetFreeBytes(string volumeGuid) => null;
}
