using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.LifecycleEvents;
using ShinyFileServe.App;
using FileServeApp = ShinyFileServe.App.App;

namespace ShinyFileServe.Windows;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<FileServeApp>()
            .ConfigureFileServe()
            .ConfigureLifecycleEvents(events => events.AddWindows(windows => windows
                .OnWindowCreated(window =>
                {
                    // Closing the console window hides it rather than ending the process: this is a
                    // tray-resident server, and Quit on the tray menu is the only way out. WinUI
                    // otherwise tears the whole app down with the last window.
                    var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                    var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);

                    appWindow.Closing += (_, args) =>
                    {
                        args.Cancel = true;
                        appWindow.Hide();
                    };
                })));

        // Windows is the one platform with an antimalware engine already present and callable, so
        // uploads are scanned through AMSI - Defender, or whatever replaced it - with nothing for the
        // operator to install. The other heads need a clamd; see ConfigureFileServe.
        builder.Services.AddAmsiScanner();

        // Started here rather than from App.CreateWindow: the tray icon has to outlive every
        // window, so it is created before the first one exists.
        return builder.Build().StartFileServe();
    }
}
