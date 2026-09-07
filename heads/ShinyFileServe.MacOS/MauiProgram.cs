using Shiny;
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ShinyFileServe.App;
using ShinyFileServe.App.Services;
using FileServeApp = ShinyFileServe.App.App;

namespace ShinyFileServe.MacOS;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder.Services.AddSingleton<IPlatformShell, MacPlatformShell>();

        // Registered per head: AddNotifications() only exists on the platform builds.
        builder.Services.AddNotifications();

        builder
            .UseMauiAppMacOS<FileServeApp>()
            .AddMacOSEssentials()
            .ConfigureFileServe();

        // Started here rather than from App.CreateWindow: the tray icon has to outlive every
        // window, so it is created before the first one exists.
        return builder.Build().StartFileServe();
    }
}
