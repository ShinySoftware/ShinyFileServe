using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ShinyFileServe.App.Services;

/// <summary>
/// Opens a URL in the user's browser. Deliberately not MAUI Essentials' Launcher: the shared UI is
/// a plain net10.0 library, where Essentials has no implementation behind it, and the GTK4 and
/// AppKit heads bring their own. Three shell commands are simpler than three package references.
/// </summary>
public static class SystemBrowser
{
    public static void Open(string url, ILogger? logger = null)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch (Exception ex)
        {
            // Failing to open a browser is a nuisance, never a reason to take the app down.
            logger?.LogWarning(ex, "Could not open {Url}", url);
        }
    }
}
