namespace ShinyFileServe.App.Services;

/// <summary>
/// The few things a tray-resident app needs from the window server that MAUI has no cross-platform
/// API for. Each head supplies its own; the default does nothing, which is the correct behaviour
/// anywhere the concept does not exist.
/// </summary>
public interface IPlatformShell
{
    /// <summary>
    /// Take the app out of the Dock / taskbar / app switcher. Called every time the console window
    /// closes, and never at startup - an app that vanished before anyone had seen it would be
    /// unreachable if the tray icon failed to appear. <see cref="BringToFront"/> undoes it, so this
    /// has to be able to run more than once.
    /// </summary>
    void EnterBackgroundMode();

    /// <summary>Bring the app forward. A background app cannot be raised by clicking anything.</summary>
    void BringToFront();

    /// <summary>
    /// End the process. Not always the same as <c>Application.Quit()</c>: a head that has turned
    /// off "terminate after the last window closes" - which is what keeps the server running when
    /// the window is closed - needs to say so explicitly, or Quit only puts the window away.
    /// </summary>
    void Quit();

    /// <summary>
    /// Put text on the clipboard. Best-effort: a head with nowhere to put it does nothing, and the
    /// caller carries on.
    /// </summary>
    void CopyToClipboard(string text);
}

public class DefaultPlatformShell : IPlatformShell
{
    public void EnterBackgroundMode() { }
    public void BringToFront() { }
    public void Quit() => Application.Current?.Quit();
    public void CopyToClipboard(string text) { }
}
