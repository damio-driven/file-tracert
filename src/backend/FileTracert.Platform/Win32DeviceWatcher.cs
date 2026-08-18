using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FileTracert.Contracts.Platform;
using FileTracert.Platform.Interop;
using Microsoft.Extensions.Logging;

namespace FileTracert.Platform;

/// <summary>
/// <see cref="IDeviceWatcher"/> on <c>CM_Register_Notification</c> with a
/// <c>GUID_DEVINTERFACE_VOLUME</c> filter. Chosen over <c>RegisterDeviceNotification</c>
/// because it needs neither a window nor a service control handle: the generic host has
/// neither, and the same code must run as a console app in dev and as a service in prod.
/// <para>
/// Windows fires a <em>burst</em> of notifications for a single physical insertion
/// (interface arrival, volume arrival, …). Deduplication belongs to the consumer; this
/// class raises what the OS says, immediately, and returns the OS thread.
/// </para>
/// </summary>
public sealed unsafe class Win32DeviceWatcher(ILogger<Win32DeviceWatcher> logger) : IDeviceWatcher
{
    private readonly ILogger<Win32DeviceWatcher> _logger = logger;

    /// <summary>Guards <see cref="Start"/> against <see cref="Dispose"/> and against itself.</summary>
    private readonly Lock _sync = new();

    /// <summary>HCMNOTIFICATION; <see cref="IntPtr.Zero"/> while unregistered.</summary>
    private IntPtr _registration;

    /// <summary>
    /// Strong handle handed to cfgmgr32 as the callback context. It has to be strong: the
    /// native registration holds a raw pointer to this instance, and a collection between
    /// registration and unregistration would let the callback dereference freed memory —
    /// a native crash, not an exception. Released in <see cref="Dispose"/>, after the
    /// unregistration has drained any in-flight callback.
    /// </summary>
    private GCHandle _self;

    private bool _disposed;

    public event EventHandler<DeviceChangeEvent>? Changed;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registration != IntPtr.Zero)
            {
                return;
            }

            var filter = new CmNotifyFilter
            {
                cbSize = (uint)sizeof(CmNotifyFilter),
                FilterType = NativeMethods.CmNotifyFilterTypeDeviceInterface,
                ClassGuid = NativeMethods.VolumeDeviceInterfaceClass,
            };

            _self = GCHandle.Alloc(this);
            int result;
            try
            {
                result = NativeMethods.CM_Register_Notification(
                    &filter,
                    (void*)GCHandle.ToIntPtr(_self),
                    &OnNativeNotification,
                    out var registration);

                if (result == NativeMethods.CrSuccess)
                {
                    _registration = registration;
                    _logger.LogInformation("Device watcher registered on GUID_DEVINTERFACE_VOLUME.");
                    return;
                }
            }
            catch
            {
                _self.Free();
                throw;
            }

            _self.Free();
            throw new InvalidOperationException(
                $"CM_Register_Notification failed with CONFIGRET 0x{result:X8}.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_registration != IntPtr.Zero)
            {
                // Blocks until in-flight callbacks have returned, so freeing the context
                // handle right after is safe. The callback never takes _sync, so this
                // cannot deadlock against a notification arriving during shutdown.
                int result = NativeMethods.CM_Unregister_Notification(_registration);
                _registration = IntPtr.Zero;

                if (result != NativeMethods.CrSuccess)
                {
                    _logger.LogWarning(
                        "CM_Unregister_Notification failed with CONFIGRET 0x{Result:X8}; " +
                        "the registration may outlive this instance.",
                        result);
                }
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            Changed = null;
        }
    }

    /// <summary>
    /// Native entry point. A function pointer, not a marshalled delegate: there is no
    /// managed object for the GC to collect out from under cfgmgr32, and the instance is
    /// reached through the context handle instead. Nothing may throw across this boundary —
    /// an escaping exception tears down the process.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnNativeNotification(
        IntPtr notification,
        void* context,
        uint action,
        void* eventData,
        uint eventDataSize)
    {
        Win32DeviceWatcher? watcher = null;
        try
        {
            watcher = GCHandle.FromIntPtr((IntPtr)context).Target as Win32DeviceWatcher;
            watcher?.Raise(action);
        }
        catch (Exception ex)
        {
            // Last resort. Raise() already logs anything a handler throws; reaching here means
            // the context handle itself did not resolve, in which case there is no instance to
            // log with — and crashing the service over a device notification would be worse.
            watcher?._logger.LogError(ex, "Device-notification callback failed (action {Action}).", action);
        }

        // ERROR_SUCCESS: only meaningful for query-remove actions, which this filter never sees.
        return 0;
    }

    /// <summary>Translates the native action and raises <see cref="Changed"/>. Never throws.</summary>
    private void Raise(uint action)
    {
        DeviceChangeKind? kind = action switch
        {
            NativeMethods.CmNotifyActionDeviceInterfaceArrival => DeviceChangeKind.Arrived,
            NativeMethods.CmNotifyActionDeviceInterfaceRemoval => DeviceChangeKind.Removed,
            // Query-remove and friends belong to other filter types: not our business.
            _ => null,
        };

        if (kind is null)
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, new DeviceChangeEvent(kind.Value, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            // The handler is expected to be non-blocking and non-throwing; if it throws anyway
            // the exception must not cross back into native code.
            _logger.LogError(ex, "A device-change handler threw for a {Kind} notification.", kind);
        }
    }
}
