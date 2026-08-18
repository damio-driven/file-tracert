using System.Threading.Channels;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Notifications;
using FileTracert.Contracts.Platform;
using FileTracert.Host.Configuration;
using FileTracert.Host.Infrastructure;
using Microsoft.Extensions.Options;

namespace FileTracert.Host.Workers;

/// <summary>
/// Turns OS device notifications into volume-sync cycles, so a drive that is plugged back in is
/// noticed at once instead of at the next poll of <see cref="VolumeSyncWorker"/> (up to a minute
/// later, during which the jobs parked on that volume simply wait).
/// <para>
/// Windows fires a burst of notifications for a single insertion; the burst is coalesced into
/// one cycle by a short debounce window. If the native registration fails the service keeps
/// working on the periodic poll alone — loudly, never silently (§9).
/// </para>
/// </summary>
public sealed class DeviceWatcherWorker : BackgroundService
{
    private readonly IDeviceWatcher _watcher;
    private readonly VolumeSyncCycle _cycle;
    private readonly IServiceProvider _services;
    private readonly TimeSpan _debounce;
    private readonly ILogger<DeviceWatcherWorker> _logger;

    /// <summary>
    /// Capacity-1, DropWrite: every notification of a burst that arrives before the worker looks
    /// collapses into the single pending "something changed" token. Also the reason the event
    /// handler can run on the OS thread — TryWrite never blocks and never throws.
    /// </summary>
    private readonly Channel<byte> _pending = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public DeviceWatcherWorker(
        IDeviceWatcher watcher,
        VolumeSyncCycle cycle,
        IServiceProvider services,
        IOptions<FileTracertOptions> options,
        ILogger<DeviceWatcherWorker> logger)
    {
        _watcher = watcher;
        _cycle = cycle;
        _services = services;
        _debounce = TimeSpan.FromMilliseconds(Math.Max(0, options.Value.DeviceChangeDebounceMilliseconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher.Changed += OnDeviceChanged;
        try
        {
            if (!await TryStartWatcherAsync(stoppingToken))
            {
                // Nothing will ever signal us: stop this worker and leave the periodic sync to it.
                return;
            }

            await ConsumeAsync(stoppingToken);
        }
        finally
        {
            _watcher.Changed -= OnDeviceChanged;
            // The DI container disposes the singleton too; Dispose is idempotent, and releasing
            // the native registration at shutdown time rather than at container teardown is what
            // §3 asks for — no registration survives the host.
            _watcher.Dispose();
        }
    }

    /// <summary>Runs one debounced sync cycle per burst until cancellation.</summary>
    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _pending.Reader.ReadAsync(stoppingToken);

                // Let the rest of the burst land, then swallow it: those notifications describe
                // the same insertion this cycle is about to observe. Anything arriving after the
                // drain stays pending and gets its own cycle — it may be a change this one missed.
                if (_debounce > TimeSpan.Zero)
                {
                    await Task.Delay(_debounce, stoppingToken);
                }

                while (_pending.Reader.TryRead(out _))
                {
                }

                await _cycle.RunAsync("device", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One failed cycle must not deafen the service to every later device change.
                _logger.LogError(ex, "Device-triggered volume sync failed; the periodic sync still runs.");
            }
        }
    }

    /// <summary>
    /// Handler for <see cref="IDeviceWatcher.Changed"/>. Runs on an OS thread: it records that
    /// something happened and returns, never doing work there.
    /// </summary>
    private void OnDeviceChanged(object? sender, DeviceChangeEvent e)
    {
        _logger.LogDebug("Device change: {Kind} at {TimestampUtc:O}.", e.Kind, e.TimestampUtc);
        _pending.Writer.TryWrite(0);
    }

    /// <summary>
    /// Registers with the OS. A failure is not fatal — volumes are still reconciled by the
    /// periodic sync — but it degrades a promise the user can feel, so it is logged in full and
    /// surfaced as a notification (§9).
    /// </summary>
    private async Task<bool> TryStartWatcherAsync(CancellationToken ct)
    {
        try
        {
            _watcher.Start();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device watcher registration failed; falling back to the periodic volume sync.");
            await PublishRegistrationFailureAsync(ex, ct);
            return false;
        }
    }

    private async Task PublishRegistrationFailureAsync(Exception ex, CancellationToken ct)
    {
        try
        {
            var interval = _services.GetRequiredService<IOptions<FileTracertOptions>>()
                .Value.VolumeSyncIntervalSeconds;

            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<INotificationPublisher>().PublishAsync(
                NotificationSeverity.Warning,
                "DeviceWatcher",
                "Rilevamento automatico dei drive non attivo",
                $"Impossibile registrarsi alle notifiche di sistema: i volumi collegati o scollegati " +
                $"vengono comunque riconosciuti entro {interval} secondi.\n\n{ex}",
                null,
                ct);
        }
        catch (Exception notifyEx)
        {
            // Notifying about a failure must not itself take the worker down.
            _logger.LogError(notifyEx, "Failed to record the device-watcher registration notification.");
        }
    }
}
