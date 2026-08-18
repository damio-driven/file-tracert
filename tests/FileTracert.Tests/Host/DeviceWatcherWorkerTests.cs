using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Host.Infrastructure;
using FileTracert.Tests.Business;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Host;

/// <summary>
/// Step 10a: the OS event is the trigger, the interval poll is only the safety net. Everything
/// here runs through the real host wiring with a fake <see cref="IDeviceWatcher"/> — the worker
/// is the component under test, the interop is covered separately.
/// </summary>
public sealed class DeviceWatcherWorkerTests
{
    private const string LiveGuid = @"\\?\Volume{33333333-3333-3333-3333-333333333333}\";

    private static ProbedVolume Live() =>
        new(LiveGuid, "SER", "Live", "NTFS", IsRemovable: true,
            MountPoints: [@"X:\"], CapacityBytes: 1000, FreeBytes: 500, PhysicalDiskId: null);

    /// <summary>Raising before the worker subscribed would simply be lost, and the test flaky.</summary>
    private static Task WaitForSubscriptionAsync(FakeDeviceWatcher watcher) =>
        TestPolling.WaitUntilAsync(() => Task.FromResult(watcher.HasSubscribers));

    /// <summary>
    /// Windows fires a burst for a single insertion (interface, volume, arrival…). The debounce
    /// window has to collapse it into one reconciliation, not one per notification.
    /// </summary>
    [Fact]
    public async Task A_burst_of_events_produces_a_single_sync_cycle()
    {
        var watcher = new FakeDeviceWatcher();
        var probe = new CountingVolumesProbe([Live()]);
        using var factory = new FileTracertAppFactory
        {
            // The interval poll would run cycles of its own and make the count meaningless.
            DisableVolumeSync = true,
            DisableScan = true,
            DisableQueue = true,
            DeviceChangeDebounceMilliseconds = 500,
            DeviceWatcher = watcher,
            Probe = probe,
        };

        using var _ = factory.CreateClient();
        await WaitForSubscriptionAsync(watcher);

        for (int i = 0; i < 8; i++)
        {
            watcher.Raise();
        }

        await TestPolling.WaitUntilAsync(() => Task.FromResult(probe.Enumerations > 0));
        // Well past the debounce window: a second cycle, had the burst produced one, would be in.
        await Task.Delay(1500);

        probe.Enumerations.Should().Be(1);
    }

    /// <summary>
    /// The point of the whole step: the drive comes back and the job parked on it returns to
    /// Pending straight away, without the periodic sync — which is switched off here.
    /// </summary>
    [Fact]
    public async Task A_device_event_revaluates_the_jobs_parked_on_the_returning_volume()
    {
        var watcher = new FakeDeviceWatcher();
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            DisableQueue = true,
            DeviceWatcher = watcher,
            Probe = new FakeVolumesProbe([Live()]),
            Seed = async (db, ct) =>
            {
                var volume = new Volume
                {
                    VolumeGuid = LiveGuid,
                    FileSystem = "NTFS",
                    ScanEngine = VolumeScanEngine.UsnJournal,
                    IsOnline = false,
                };
                db.Volumes.Add(volume);
                await db.SaveChangesAsync(ct);

                db.OperationJobs.Add(new OperationJob
                {
                    Type = JobType.CreateFolder,
                    State = JobState.Blocked,
                    BlockReason = JobBlockReason.TargetVolumeOffline,
                    IsIntraVolume = true,
                    TargetVolumeId = volume.Id,
                    TargetRelativePath = @"Archivio\Nuova",
                    SequenceOrder = 1,
                    ErrorMessage = "volume scollegato",
                });
                await db.SaveChangesAsync(ct);
            },
        };

        using var _ = factory.CreateClient();
        await WaitForSubscriptionAsync(watcher);

        watcher.Raise();

        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
            var job = await db.OperationJobs.AsNoTracking().SingleOrDefaultAsync();
            return job is { State: JobState.Pending, BlockReason: JobBlockReason.None };
        });
    }

    /// <summary>
    /// The two triggers share one cycle and one gate: the second caller waits for the first to
    /// finish, and is never dropped — a cycle already running may have enumerated the volumes
    /// before the drive that triggered the second one appeared.
    /// </summary>
    [Fact]
    public async Task Interval_and_device_cycles_never_overlap()
    {
        var probe = new LatchingVolumesProbe();
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            DisableQueue = true,
            DisableDeviceWatcher = true,
            Probe = probe,
        };

        using var _ = factory.CreateClient();
        var cycle = factory.Services.GetRequiredService<VolumeSyncCycle>();

        // Off the test thread on purpose: the probe is a synchronous port, so the first part of a
        // cycle runs on whoever called it — here that would be this thread, blocked in the latch.
        var interval = Task.Run(() => cycle.RunAsync("interval", CancellationToken.None));
        await probe.EnteredAsync();

        var device = Task.Run(() => cycle.RunAsync("device", CancellationToken.None));
        await Task.Delay(300);

        device.IsCompleted.Should().BeFalse("the second cycle must wait on the gate");
        probe.Enumerations.Should().Be(1, "only one cycle may be inside the platform call");

        probe.Release();
        await Task.WhenAll(interval, device);

        probe.MaxConcurrent.Should().Be(1);
        probe.Enumerations.Should().Be(2, "the second cycle waits, it is not dropped");
    }

    /// <summary>
    /// §9: a refused registration degrades the service to polling, loudly. The host must still
    /// start, the periodic sync must still bring the volume online, and the user must be told.
    /// </summary>
    [Fact]
    public async Task Failed_registration_falls_back_to_polling_and_warns_the_user()
    {
        var watcher = new FakeDeviceWatcher
        {
            StartFailure = new InvalidOperationException("CM_Register_Notification failed with CONFIGRET 0x0000000D."),
        };
        using var factory = new FileTracertAppFactory
        {
            DisableScan = true,
            DisableQueue = true,
            DeviceWatcher = watcher,
            Probe = new FakeVolumesProbe([Live()]),
        };

        using var _ = factory.CreateClient();

        await TestPolling.WaitUntilAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();

            // The interval poll still reconciles the volume…
            bool online = await db.Volumes.AsNoTracking().AnyAsync(v => v.VolumeGuid == LiveGuid && v.IsOnline);
            // …and the failure is visible to the user, not just in the log.
            bool warned = await db.Notifications.AsNoTracking().AnyAsync(n =>
                n.Source == "DeviceWatcher" && n.Severity == NotificationSeverity.Warning);
            return online && warned;
        });

        watcher.StartCount.Should().Be(1);
    }

    /// <summary>Shutdown must release the native registration, not leave it to the finalizer.</summary>
    [Fact]
    public async Task Shutdown_disposes_the_watcher()
    {
        var watcher = new FakeDeviceWatcher();
        var factory = new FileTracertAppFactory
        {
            DisableScan = true,
            DisableQueue = true,
            DeviceWatcher = watcher,
            Probe = new FakeVolumesProbe([Live()]),
        };

        using (var _ = factory.CreateClient())
        {
            await WaitForSubscriptionAsync(watcher);
        }

        factory.Dispose();

        watcher.DisposeCount.Should().BeGreaterThan(0);
    }
}
