using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Power;

/// <summary>
/// Holds off the operating system's idle sleep according to <see cref="KeepAwakeMode"/>. The server
/// only reports on it; the desktop app supplies it, because power assertions belong to the process
/// the machine is running, not to a web request.
/// </summary>
public interface IKeepAwake
{
    /// <summary>The stored mode, and what this machine is doing about it right now.</summary>
    KeepAwakeInfo Current { get; }

    /// <summary>Looks at the stored mode and the power source again, and takes or lets go of the hold to match.</summary>
    void Refresh();
}

/// <summary>For a host with no way to keep the machine awake - tests, or a server run without the app.</summary>
public class NoKeepAwake : IKeepAwake
{
    public KeepAwakeInfo Current { get; } = new(KeepAwakeMode.OsDefault, false, false, false, false);
    public void Refresh() { }
}
