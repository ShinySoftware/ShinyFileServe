using AppKit;
using Foundation;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace ShinyFileServe.MacOS;

[Register("MauiMacOSApp")]
public class MauiMacOSApp : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// Closing the console window puts it away; the tray icon is how you get it back and the only
    /// way out. Without this the whole server would stop the first time someone hit the red button.
    /// </summary>
    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => false;
}
