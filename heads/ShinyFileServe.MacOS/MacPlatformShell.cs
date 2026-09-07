using AppKit;
using ShinyFileServe.App.Services;

namespace ShinyFileServe.MacOS;

/// <summary>
/// The AppKit half of the tray-resident behaviour. Accessory takes the app out of the Dock and the
/// app switcher, and - just as importantly - removes the "quit when the last window closes" rule,
/// so closing the console window leaves the server running behind the menu bar icon.
/// </summary>
public class MacPlatformShell : IPlatformShell
{
    public void EnterBackgroundMode()
        => NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Accessory;

    public void BringToFront()
    {
        // An Accessory app has nothing to click in the Dock, so it has to raise itself.
        NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
    }

    /// <summary>
    /// Straight to AppKit rather than through <c>Application.Quit()</c>. MAUI's Quit closes the
    /// windows and then leaves the decision to AppKit's terminate-after-last-window rule - which
    /// MauiMacOSApp deliberately turns off so the server outlives the window. Without this, Quit
    /// on the tray menu would do exactly what the close button already does.
    /// </summary>
    public void Quit() => NSApplication.SharedApplication.Terminate(null!);

    public void CopyToClipboard(string text)
    {
        var pasteboard = NSPasteboard.GeneralPasteboard;
        pasteboard.ClearContents();
        pasteboard.SetStringForType(text, NSPasteboard.NSPasteboardTypeString);
    }
}
