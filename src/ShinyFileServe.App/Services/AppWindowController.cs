using Microsoft.Extensions.DependencyInjection;

namespace ShinyFileServe.App.Services;

/// <summary>
/// Shows and hides the console window. The app is a tray-resident server: closing the window puts
/// it away rather than shutting the server down, and the only way out is Quit on the tray menu.
/// </summary>
public interface IAppWindowController
{
    bool IsWindowOpen { get; }

    /// <summary>
    /// Opens the window if it was put away, and brings it forward either way. There is deliberately
    /// no Hide: the close button is how a window goes away, and programmatic CloseWindow disconnects
    /// the handler before AppKit reports the close, which the current macOS backend does not expect.
    /// </summary>
    void Show();

    /// <summary>Ends the process for real.</summary>
    void Quit();

    /// <summary>Builds the console window and starts tracking it. Called by <see cref="App"/>.</summary>
    Window CreateWindow();
}

public class AppWindowController(IPlatformShell shell, IServiceProvider services) : IAppWindowController
{
    Window? window;
    bool everClosed;

    public bool IsWindowOpen => this.window is not null;

    public Window CreateWindow()
    {
        var created = new Window(ActivatorUtilities.CreateInstance<ConsoleWindowPage>(services))
        {
            Title = "FileServe",
            Width = 1180,
            Height = 780,
            MinimumWidth = 900,
            MinimumHeight = 600
        };

        this.Track(created);
        return created;
    }

    void Track(Window created)
    {
        this.window = created;

        created.Destroying += (_, _) =>
        {
            // Nothing is torn down beyond forgetting the reference: the server keeps serving and
            // Show() builds a fresh window from the same container when it is next asked for.
            this.window = null;

            // The app leaves the Dock the first time its window is closed rather than at launch,
            // so it is always reachable the normal way until the user has actually put it away.
            if (!this.everClosed)
            {
                this.everClosed = true;
                shell.EnterBackgroundMode();
            }
        };
    }

    public void Show()
    {
        var application = Application.Current;
        if (application is null)
            return;

        shell.BringToFront();

        if (this.window is { } existing)
        {
            application.ActivateWindow(existing);
            return;
        }

        application.OpenWindow(this.CreateWindow());
    }

    public void Quit()
    {
        this.window = null;
        shell.Quit();
    }
}
