using Shiny;

namespace ShinyFileServe.Headless;

/// <summary>
/// Start at login means nothing to a process the container runtime or a service manager already
/// starts. Reporting NotSupported is what makes the portal leave the switch out.
/// </summary>
class NoAppStartupInstaller : IAppStartupInstaller
{
    public bool IsSupported => false;
    public Task<AppStartupState> GetState(CancellationToken cancellationToken = default) => Task.FromResult(AppStartupState.NotSupported);
    public Task<AppStartupState> Register(CancellationToken cancellationToken = default) => Task.FromResult(AppStartupState.NotSupported);
    public Task<AppStartupState> Unregister(CancellationToken cancellationToken = default) => Task.FromResult(AppStartupState.NotSupported);
    public Task<bool> OpenSettings() => Task.FromResult(false);
}
