using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Updates;

/// <summary>
/// New releases of the app itself, as opposed to the frontend the web app host updates on its own.
/// The server only exposes this to administrators; the desktop app supplies it, because replacing
/// the app and restarting it are things only the app can do to itself.
/// </summary>
public interface IAppUpdater
{
    AppUpdateInfo Current { get; }

    /// <summary>Raised on every state change, from any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Asks GitHub again. Never throws - a failed check is a state, not an error.</summary>
    Task<AppUpdateInfo> Check(CancellationToken ct = default);

    /// <summary>"Don't show again" for the release on offer, or undoing it.</summary>
    Task<AppUpdateInfo> Skip(bool skip, CancellationToken ct = default);

    /// <summary>
    /// Starts downloading, verifying and installing the release on offer, then restarts the app.
    /// Returns once it has started; progress is <see cref="Current"/>. Null when there is nothing this
    /// machine can install, with the reason on <see cref="AppUpdateInfo.CannotInstallReason"/>.
    /// </summary>
    AppUpdateInfo? BeginInstall();
}

/// <summary>For a host that has no way to update itself - tests, or a server run without the app.</summary>
public class NoAppUpdater : IAppUpdater
{
    public AppUpdateInfo Current { get; } = new(
        "", AppUpdateState.Idle, false, null, null, null, null, null, null,
        false, null, false, null, null, null
    );

    public event EventHandler? Changed { add { } remove { } }

    public Task<AppUpdateInfo> Check(CancellationToken ct = default) => Task.FromResult(this.Current);
    public Task<AppUpdateInfo> Skip(bool skip, CancellationToken ct = default) => Task.FromResult(this.Current);
    public AppUpdateInfo? BeginInstall() => null;
}
