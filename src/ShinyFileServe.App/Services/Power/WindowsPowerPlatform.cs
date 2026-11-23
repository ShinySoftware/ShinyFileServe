using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ShinyFileServe.App.Services.Power;

/// <summary>
/// A power request rather than SetThreadExecutionState: the latter belongs to the thread that called
/// it, and this is called from timer threads that come and go. A request is a handle, held for as
/// long as it is set, shown in <c>powercfg /requests</c> under the reason given, and released by
/// Windows when the process ends.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerPlatform : IPowerPlatform
{
    const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    const int PowerRequestSystemRequired = 1;
    const byte BatteryFlagNoSystemBattery = 128;
    const byte BatteryFlagUnknown = 255;
    const byte AcLineOffline = 0;

    IntPtr request;
    bool held;

    public bool IsSupported => true;

    public PowerSource ReadPowerSource()
    {
        if (!GetSystemPowerStatus(out var status))
            return new(false, false);

        var hasBattery = (status.BatteryFlag & BatteryFlagNoSystemBattery) == 0 && status.BatteryFlag != BatteryFlagUnknown;
        return new(hasBattery, hasBattery && status.ACLineStatus == AcLineOffline);
    }

    public bool SetHold(bool hold)
    {
        if (hold == this.held)
            return hold;

        if (this.request == IntPtr.Zero)
        {
            var context = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = "FileServe is keeping this machine awake to serve files"
            };

            this.request = PowerCreateRequest(ref context);
            if (this.request == IntPtr.Zero || this.request == new IntPtr(-1))
            {
                this.request = IntPtr.Zero;
                return false;
            }
        }

        this.held = hold
            ? PowerSetRequest(this.request, PowerRequestSystemRequired)
            : !PowerClearRequest(this.request, PowerRequestSystemRequired);

        return this.held;
    }

    public void Dispose()
    {
        this.SetHold(false);

        if (this.request != IntPtr.Zero)
        {
            CloseHandle(this.request);
            this.request = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
