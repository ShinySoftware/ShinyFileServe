using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny;
using ShinyFileServe.App.Services;
using ShinyFileServe.App.Services.Power;
using ShinyFileServe.Core;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server;
using ShinyFileServe.Server.Power;
using ShinyFileServe.Server.Updates;

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

        // The tray icon. The rest of what it adds (hot keys, docking) goes unused.
        builder.UseShinyDesktop();

        // Shiny.Notifications registers per platform - AddNotifications() only exists on the
        // platform builds - so each head calls it. ActivityNotifier resolves the manager if a head
        // supplied one and falls back to the tray icon's own balloon if not.

        builder.Services.AddFileServeCore(o =>
        {
            // No account is seeded. The first person to open the portal creates the administrator.
            o.DatabasePath = AppPaths.DatabaseFile;
        });

        // FILESERVE_CLAMD and friends - see AddFileScanningFromEnvironment.
        builder.Services.AddFileScanningFromEnvironment();

        builder.Services.AddFileServeServer(
            o =>
            {
                // Always every interface: this is a LAN server, and the tunnel is the separate,
                // deliberate step for reaching it from outside. The port is a placeholder until
                // the stored settings are read - see ServerController.Initialize.
                o.Address = IPAddress.Any;
                o.Port = 8080;
                o.TicketKeyPath = AppPaths.TicketKeyFile;
                o.CertificatePath = AppPaths.CertificateFile;

                // Frontend updates, when a release server is configured, live beside the database.
                o.DataDirectory = AppPaths.WebAppDirectory;
                o.PackagesDirectory = AppPaths.PackagesDirectory;
                o.UploadsDirectory = AppPaths.UploadsDirectory;
            },
            // The operator owns the switch, from the window or the tray. Auto-start is honoured
            // after startup rather than during registration, so a bind failure is visible.
            autoStart: false
        );

        // One call for all three heads. Shiny picks the mechanism from the TFM the head resolved -
        // the HKCU Run key on Windows, an SMAppService login item on macOS, an XDG autostart entry
        // on the plain net10.0 Linux head - so there is nothing per-head to write or keep in step.
        builder.Services.AddAppStartupInstaller(o =>
        {
            // Named rather than left to be inferred: the identifier becomes the Run value name and
            // the .desktop file name, so it has to survive an upgrade, and MAUI Essentials' AppInfo
            // is not answerable on the GTK4 head to be inferred from anyway.
            o.Identifier = "net.shinylib.fileserve";
            o.DisplayName = "FileServe";
        });

        builder.Services.AddSingleton<ServerController>();
        builder.Services.TryAddSingleton<IPlatformShell, DefaultPlatformShell>();
        builder.Services.AddSingleton<IAppWindowController, AppWindowController>();
        builder.Services.AddSingleton<TrayController>();
        builder.Services.AddSingleton<ActivityNotifier>();

        // Added rather than tried: the server registered a do-nothing updater for hosts without one,
        // and the last registration is the one that resolves.
        builder.Services.AddSingleton<AppUpdater>();
        builder.Services.AddSingleton<IAppUpdater>(sp => sp.GetRequiredService<AppUpdater>());

        // Added rather than tried, for the same reason: the server registered one that does nothing.
        builder.Services.AddSingleton<KeepAwakeService>();
        builder.Services.AddSingleton<IKeepAwake>(sp => sp.GetRequiredService<KeepAwakeService>());

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
        app.Services.GetRequiredService<AppUpdater>().Start();
        app.Services.GetRequiredService<KeepAwakeService>().Start();
        return app;
    }
}
