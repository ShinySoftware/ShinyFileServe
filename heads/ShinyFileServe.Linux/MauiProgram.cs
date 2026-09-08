using Shiny;
using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ShinyFileServe.App;
using FileServeApp = ShinyFileServe.App.App;

namespace ShinyFileServe.Linux;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        // Registered per head: on Linux the implementation ships in Shiny.Notifications.Linux.
        builder.Services.AddNotifications();

        builder
            .UseMauiAppLinuxGtk4<FileServeApp>()
            .ConfigureFileServe();

        // Started here rather than from App.CreateWindow: the tray icon has to outlive every
        // window, so it is created before the first one exists.
        return builder.Build().StartFileServe();
    }
}
