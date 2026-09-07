using System.Reflection;
using Microsoft.Extensions.Logging;
using Shiny.Maui.Controls.Desktop.TrayIcon;
using ShinyFileServe.Server;

namespace ShinyFileServe.App.Services;

/// <summary>
/// The tray icon, which is the app's real front door - the window is optional. The menu carries the
/// address people need, a switch for the server, a switch for the public tunnel, and the only Quit
/// in the application.
/// </summary>
public class TrayController(
    ITrayIconFactory factory,
    ServerController server,
    IAppWindowController windows,
    IPlatformShell shell,
    ILogger<TrayController> logger
) : IDisposable
{
    ITrayIcon? icon;
    TrayMenuItem? addressItem;
    TrayMenuItem? serverItem;
    TrayMenuItem? tunnelItem;

    /// <summary>Null until the first paint, so the artwork is only re-decoded when the state moves.</summary>
    bool? showingBusy;

    public bool IsAvailable => this.icon is not null;

    public void Start()
    {
        if (this.icon is not null)
            return;

        try
        {
            this.icon = factory.Create();
        }
        catch (PlatformNotSupportedException ex)
        {
            // Nothing to fall back to, and nothing fatal either - the window still works.
            logger.LogWarning(ex, "This platform has no tray icon; running with the window only");
            return;
        }

        this.icon.Tooltip = "FileServe";

        // Reads out the address and acts on it. A tray line that shows a url is a promise it can
        // also hand it over, so a click opens the browser there and leaves it on the clipboard -
        // one of those is what the person who opened this menu came for, and which one depends on
        // whether the machine they are sending it to is the one in front of them.
        this.addressItem = new TrayMenuItem("Not serving", this.OnAddressClicked) { IsEnabled = false };
        this.serverItem = new TrayMenuItem("Start server", () => _ = server.ToggleServer());
        this.tunnelItem = new TrayMenuItem("Open public tunnel", () => _ = server.ToggleTunnel());

        this.icon.SetMenu(TrayMenu.Build(b => b
            .Item(this.addressItem)
            .Separator()
            .Item(this.serverItem)
            .Item(this.tunnelItem)
            .Separator()
            .Item(new TrayMenuItem("Open FileServe", windows.Show))
            .Separator()
            .Item(new TrayMenuItem("Quit FileServe", this.Quit) { Accelerator = "Ctrl+Q" })
        ));

        this.icon.PrimaryClick += (_, _) => windows.Show();

        server.Changed += this.OnServerChanged;
        this.Refresh();

        // Kicked off here rather than from a page: auto-start has to happen whether or not anyone
        // ever opens the window, and the tray is the one thing guaranteed to exist.
        _ = server.Initialize();
    }

    void OnServerChanged(object? sender, EventArgs args)
    {
        // Menu labels are read by the platform handler, which rebuilds the native menu off a
        // property change - so this has to be on the UI thread like any other visual mutation.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.IsDispatchRequired)
            this.Refresh();
        else
            dispatcher.Dispatch(this.Refresh);
    }

    void Refresh()
    {
        if (this.icon is null)
            return;

        this.PaintIcon();

        if (this.addressItem is { } address)
        {
            // The tunnel first when it is up, because it is the address that works from anywhere.
            address.Label = server.PreferredUrl ?? (server.IsServing ? "Serving, no address found" : "Not serving");

            // Clicking the address is only useful when there is one to open.
            address.IsEnabled = server.PreferredUrl is not null;
        }

        if (this.serverItem is { } serverToggle)
            serverToggle.Label = server.IsServing ? "Stop server" : "Start server";

        if (this.tunnelItem is { } tunnelToggle)
        {
            tunnelToggle.Label = server.TunnelState switch
            {
                TunnelState.Open => "Close public tunnel",
                TunnelState.Connecting => "Opening tunnel…",
                TunnelState.Reconnecting => "Reconnecting tunnel…",
                TunnelState.Failed => "Retry public tunnel",
                _ => "Open public tunnel"
            };

            // A tunnel forwards to the listener, so there is nothing to open without one.
            tunnelToggle.IsEnabled = server.IsServing;
        }

        var people = server.ActiveSessionCount;
        var who = people switch
        {
            0 => null,
            1 => "1 person connected",
            _ => $"{people} people connected"
        };

        this.icon.Tooltip = (server.PreferredUrl, who) switch
        {
            ({ } url, { } connected) => $"FileServe — {url} · {connected}",
            ({ } url, null) => $"FileServe — {url}",
            _ => "FileServe — not serving"
        };
    }

    /// <summary>
    /// Grey when nobody is on it, green when somebody is - the one thing worth knowing at a glance
    /// from a menu bar. The idle mark is a template image so macOS tints it like every other icon
    /// up there; the busy one is not, because a template image would throw the colour away.
    /// </summary>
    void PaintIcon()
    {
        if (this.icon is null)
            return;

        var busy = server.ActiveSessionCount > 0;
        if (this.showingBusy == busy)
            return;

        this.showingBusy = busy;
        this.icon.IsTemplateImage = !busy;
        this.icon.SetIcon(busy ? TrayArtwork.Busy : TrayArtwork.Idle);
    }

    /// <summary>
    /// A system notification through the tray's own subsystem - Action Center on Windows,
    /// Notification Center on macOS, libnotify on Linux. Used when no notification manager is
    /// registered on this head. Silently does nothing without a tray icon.
    /// </summary>
    public void ShowNotification(string title, string message)
    {
        try
        {
            this.icon?.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show a tray notification");
        }
    }

    void OnAddressClicked()
    {
        if (server.PreferredUrl is not { } url)
            return;

        shell.CopyToClipboard(url);
        SystemBrowser.Open(url, logger);
    }

    void Quit()
    {
        this.Dispose();
        windows.Quit();
    }

    public void Dispose()
    {
        server.Changed -= this.OnServerChanged;

        // An orphaned tray icon can outlive the process on Windows until the next reboot.
        this.icon?.Dispose();
        this.icon = null;
        GC.SuppressFinalize(this);
    }
}

static class TrayArtwork
{
    /// <summary>
    /// The tray marks, embedded rather than loaded from app package assets: the three heads lay out
    /// their bundles differently and a missing file would mean no icon at all.
    /// </summary>
    public static Stream Idle() => Open("trayicon.png");

    public static Stream Busy() => Open("trayicon-active.png");

    static Stream Open(string fileName)
    {
        var assembly = typeof(TrayArtwork).Assembly;
        var name = assembly
            .GetManifestResourceNames()
            .First(x => x.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        return assembly.GetManifestResourceStream(name)!;
    }
}
