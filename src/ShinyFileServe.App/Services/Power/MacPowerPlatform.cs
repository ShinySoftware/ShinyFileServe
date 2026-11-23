using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ShinyFileServe.App.Services.Power;

/// <summary>
/// An IOKit power assertion - the same mechanism caffeinate and Amphetamine use. PreventUserIdleSystemSleep
/// rather than PreventSystemSleep: the latter is ignored on battery, which would make "even on battery" a lie.
/// The display is still allowed to sleep; only the machine is kept up. macOS releases an assertion when
/// the process that took it exits, so a crash cannot leave the machine awake.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacPowerPlatform : IPowerPlatform
{
    const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const uint kCFStringEncodingUTF8 = 0x08000100;
    const uint kIOPMAssertionLevelOn = 255;

    uint? assertion;

    public bool IsSupported => true;

    public PowerSource ReadPowerSource()
    {
        var blob = IOPSCopyPowerSourcesInfo();
        if (blob == IntPtr.Zero)
            return new(false, false);

        try
        {
            // Internal batteries and attached UPSes both appear here; a desktop Mac with neither has none.
            var list = IOPSCopyPowerSourcesList(blob);
            var count = list == IntPtr.Zero ? 0 : CFArrayGetCount(list);
            if (list != IntPtr.Zero)
                CFRelease(list);

            // Not owned - IOPSGetProvidingPowerSourceType follows the Get rule.
            var providing = ToManaged(IOPSGetProvidingPowerSourceType(blob));

            // "AC Power", "Battery Power" or "UPS Power". Anything but AC is running off stored charge.
            var onBattery = providing is not null && providing != "AC Power";
            return new(count > 0, onBattery);
        }
        finally
        {
            CFRelease(blob);
        }
    }

    public bool SetHold(bool hold)
    {
        if (hold == this.assertion.HasValue)
            return hold;

        if (!hold)
        {
            IOPMAssertionRelease(this.assertion!.Value);
            this.assertion = null;
            return false;
        }

        var type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", kCFStringEncodingUTF8);
        var name = CFStringCreateWithCString(IntPtr.Zero, "FileServe is keeping this machine awake to serve files", kCFStringEncodingUTF8);
        try
        {
            var result = IOPMAssertionCreateWithName(type, kIOPMAssertionLevelOn, name, out var id);
            if (result == 0)
                this.assertion = id;
        }
        finally
        {
            CFRelease(type);
            CFRelease(name);
        }

        return this.assertion.HasValue;
    }

    public void Dispose() => this.SetHold(false);

    static string? ToManaged(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero)
            return null;

        var buffer = new byte[256];
        return CFStringGetCString(cfString, buffer, buffer.Length, kCFStringEncodingUTF8)
            ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var end and >= 0 ? end : buffer.Length)
            : null;
    }

    [DllImport(IOKit)]
    static extern int IOPMAssertionCreateWithName(IntPtr assertionType, uint level, IntPtr assertionName, out uint assertionId);

    [DllImport(IOKit)]
    static extern int IOPMAssertionRelease(uint assertionId);

    [DllImport(IOKit)]
    static extern IntPtr IOPSCopyPowerSourcesInfo();

    [DllImport(IOKit)]
    static extern IntPtr IOPSCopyPowerSourcesList(IntPtr blob);

    [DllImport(IOKit)]
    static extern IntPtr IOPSGetProvidingPowerSourceType(IntPtr blob);

    [DllImport(CoreFoundation, CharSet = CharSet.Ansi)]
    static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    static extern void CFRelease(IntPtr value);
}
