using System.Runtime.InteropServices;

namespace FileTracert.Platform.Interop;

/// <summary>
/// Device-notification interop (<c>cfgmgr32.dll</c>, Windows 8+). Callback-based:
/// unlike <c>RegisterDeviceNotification</c> it needs neither an HWND nor the handle
/// returned by <c>RegisterServiceCtrlHandlerEx</c>, so it behaves identically in a
/// console host (dev) and inside a Windows Service (prod).
/// </summary>
internal static partial class NativeMethods
{
    /// <summary><c>CR_SUCCESS</c> — every other CONFIGRET is a failure.</summary>
    internal const int CrSuccess = 0;

    /// <summary><c>CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE</c>.</summary>
    internal const uint CmNotifyFilterTypeDeviceInterface = 0;

    // CM_NOTIFY_ACTION values relevant to a device-interface filter. The remaining
    // ones (query-remove, remove-pending, custom event, …) also reach the callback
    // for other filter types and are ignored.
    internal const uint CmNotifyActionDeviceInterfaceArrival = 0;
    internal const uint CmNotifyActionDeviceInterfaceRemoval = 1;

    /// <summary><c>GUID_DEVINTERFACE_VOLUME</c> — the device-interface class of mounted volumes.</summary>
    internal static readonly Guid VolumeDeviceInterfaceClass = new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Register_Notification")]
    internal static unsafe partial int CM_Register_Notification(
        CmNotifyFilter* pFilter,
        void* pContext,
        delegate* unmanaged[Stdcall]<IntPtr, void*, uint, void*, uint, uint> pCallback,
        out IntPtr pNotifyContext);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Unregister_Notification")]
    internal static partial int CM_Unregister_Notification(IntPtr notifyContext);
}

/// <summary>
/// <c>CM_NOTIFY_FILTER</c>. The native union is dominated by
/// <c>DeviceInstance.InstanceId[MAX_DEVICE_ID_LEN]</c> (200 WCHARs = 400 bytes); only the
/// <c>DeviceInterface.ClassGuid</c> member is used here. Explicit layout with an explicit
/// <c>Size</c> reserves the rest of the union so that <c>cbSize</c> is what cfgmgr32 expects
/// (416 bytes on both x86 and x64) without declaring padding fields nobody reads.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = HeaderBytes + UnionBytes)]
internal struct CmNotifyFilter
{
    /// <summary>cbSize + Flags + FilterType + Reserved.</summary>
    private const int HeaderBytes = 16;

    /// <summary>Bytes of the native union: <c>sizeof(WCHAR) * MAX_DEVICE_ID_LEN</c>.</summary>
    private const int UnionBytes = 2 * 200;

    [FieldOffset(0)] internal uint cbSize;
    [FieldOffset(4)] internal uint Flags;
    [FieldOffset(8)] internal uint FilterType;
    [FieldOffset(12)] internal uint Reserved;

    /// <summary>First union member: <c>DeviceInterface.ClassGuid</c>.</summary>
    [FieldOffset(HeaderBytes)] internal Guid ClassGuid;
}
