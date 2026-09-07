using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny;
using ShinyFileServe.App.Services;
using ShinyFileServe.Core;
using ShinyFileServe.Server;

namespace ShinyFileServe.App;

public static class MauiProgram
{
    /// <summary>
    /// Everything the three heads share. Each head calls this after its own
    /// <c>UseMauiAppMacOS/UseMauiAppLinuxGtk4/UseMauiApp</c> so the platform backend is chosen first.
    /// </summary>
    public static MauiAppBuilder ConfigureFileServe(this MauiAppBuilder builder)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);

        builder.UseTrayIcon();

        // Shiny.Notifications registers per platform - AddNotifications() only exists on the
        // platform builds - so each head calls it. ActivityNotifier resolves the manager if a head
        // supplied one and falls back to the tray icon's own balloon if not.

        builder.Services.AddFileServeCore(o =>
        {
            // No account is seeded. The first person to open the portal creates the administrator.
            o.DatabasePath = AppPaths.DatabaseFile;
        });

        builder.Services.AddFileServeServer(
            o =>
            {
                // Always every interface: this is a LAN server, and the tunnel is the separate,
                // deliberate step for reaching it from outside. The port is a placeholder until
                // the stored settings are read - see ServerController.Initialize.
                o.Address = IPAddress.Any;
                o.Port = 8080;
                o.TicketKeyPath = AppPaths.TicketKeyFile;
            },
            // The operator owns the switch, from the window or the tray. Auto-start is honoured
            // after startup rather than during registration, so a bind failure is visible.
            autoStart: false
        );

        builder.Services.AddSingleton<ServerController>();
        builder.Services.TryAddSingleton<IPlatformShell, DefaultPlatformShell>();
        builder.Services.AddSingleton<IAppWindowController, AppWindowController>();
        builder.Services.AddSingleton<TrayController>();
        builder.Services.AddSingleton<ActivityNotifier>();

        builder.Services.AddTransient<ConsoleWindowPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder;
    }

    /// <summary>
    /// Starts the pieces that outlive every window. Called by each head straight after
    /// <c>builder.Build()</c> - the tray icon has to exist before any window does, because the
    /// whole point of it is to still be there once the last one is closed.
    /// </summary>
    public static MauiApp StartFileServe(this MauiApp app)
    {
        app.Services.GetRequiredService<TrayController>().Start();
        app.Services.GetRequiredService<ActivityNotifier>().Start();
        return app;
    }
}
