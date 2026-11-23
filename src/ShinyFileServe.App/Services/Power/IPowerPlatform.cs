namespace ShinyFileServe.App.Services.Power;

/// <summary>The machine's answer about where its power is coming from.</summary>
public readonly record struct PowerSource(bool HasBattery, bool IsOnBattery);

/// <summary>
/// One operating system's way of holding off idle sleep and reading the power source. Everything
/// runs in the one net10.0 App assembly, so each platform is chosen at runtime rather than per head.
/// </summary>
public interface IPowerPlatform : IDisposable
{
    bool IsSupported { get; }

    PowerSource ReadPowerSource();

    /// <summary>Takes or lets go of the hold. Called repeatedly with the same answer; must be idempotent. Returns whether it is held afterwards.</summary>
    bool SetHold(bool hold);

    public static IPowerPlatform Create()
    {
        if (OperatingSystem.IsMacOS())
            return new MacPowerPlatform();

        if (OperatingSystem.IsWindows())
            return new WindowsPowerPlatform();

        if (OperatingSystem.IsLinux())
            return new LinuxPowerPlatform();

        return new UnsupportedPowerPlatform();
    }
}

public sealed class UnsupportedPowerPlatform : IPowerPlatform
{
    public bool IsSupported => false;
    public PowerSource ReadPowerSource() => new(false, false);
    public bool SetHold(bool hold) => false;
    public void Dispose() { }
}
