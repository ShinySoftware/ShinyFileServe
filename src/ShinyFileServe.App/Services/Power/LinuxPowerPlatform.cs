using System.Diagnostics;
using System.Runtime.Versioning;

namespace ShinyFileServe.App.Services.Power;

/// <summary>
/// A systemd-logind block inhibitor on idle and sleep, held by a <c>systemd-inhibit</c> child for as
/// long as it runs. The child waits on <c>tail --pid</c> rather than <c>sleep infinity</c>, so it exits
/// with this process even after a crash - Linux does not take children down with their parent, and an
/// orphaned inhibitor would keep the machine awake until somebody found it. Machines without systemd
/// have no inhibitor to take, and say so.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPowerPlatform : IPowerPlatform
{
    const string PowerSupplies = "/sys/class/power_supply";
    static readonly string[] InhibitorPaths = ["/usr/bin/systemd-inhibit", "/bin/systemd-inhibit"];

    readonly string? inhibitor = InhibitorPaths.FirstOrDefault(File.Exists);
    Process? holder;

    public bool IsSupported => this.inhibitor is not null;

    public PowerSource ReadPowerSource()
    {
        if (!Directory.Exists(PowerSupplies))
            return new(false, false);

        var supplies = Directory.GetDirectories(PowerSupplies)
            .Select(x => (Type: Read(x, "type"), Scope: Read(x, "scope"), Online: Read(x, "online"), Status: Read(x, "status")))
            // Scope=Device is a mouse or a headset reporting its own battery, not the machine's.
            .Where(x => !String.Equals(x.Scope, "Device", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var batteries = supplies.Where(x => x.Type == "Battery").ToList();
        if (batteries.Count == 0)
            return new(false, false);

        var mains = supplies.Where(x => x.Type is "Mains" or "USB" or "USB_C" or "USB_PD").ToList();
        var onBattery = mains.Count > 0
            ? mains.All(x => x.Online != "1")
            // No adapter reported: fall back to whether any battery says it is draining.
            : batteries.Any(x => x.Status == "Discharging");

        return new(true, onBattery);
    }

    public bool SetHold(bool hold)
    {
        var running = this.holder is { HasExited: false };
        if (hold == running)
            return hold;

        if (!hold)
        {
            this.Release();
            return false;
        }

        if (this.inhibitor is null)
            return false;

        try
        {
            var info = new ProcessStartInfo(this.inhibitor)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("--what=idle:sleep");
            info.ArgumentList.Add("--who=FileServe");
            info.ArgumentList.Add("--why=Keeping this machine awake to serve files");
            info.ArgumentList.Add("--mode=block");
            info.ArgumentList.Add("tail");
            info.ArgumentList.Add($"--pid={Environment.ProcessId}");
            info.ArgumentList.Add("-f");
            info.ArgumentList.Add("/dev/null");

            this.holder = Process.Start(info);
        }
        catch (Exception)
        {
            this.holder = null;
        }

        return this.holder is { HasExited: false };
    }

    public void Dispose() => this.Release();

    void Release()
    {
        var process = this.holder;
        this.holder = null;

        if (process is null)
            return;

        try
        {
            // The whole tree: killing systemd-inhibit alone would leave tail behind until this process exits.
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }

        process.Dispose();
    }

    static string? Read(string directory, string name)
    {
        try
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
