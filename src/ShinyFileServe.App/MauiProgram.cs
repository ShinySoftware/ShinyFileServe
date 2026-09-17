using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny;
using ShinyFileServe.App.Services;
using ShinyFileServe.Core;
using ShinyFileServe.Core.Services;
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

        ConfigureScanning(builder.Services);

        builder.Services.AddFileServeServer(
            o =>
            {
                // Always every interface: this is a LAN server, and the tunnel is the separate,
                // deliberate step for reaching it from outside. The port is a placeholder until
                // the stored settings are read - see ServerController.Initialize.
                o.Address = IPAddress.Any;
                o.Port = 8080;
                o.TicketKeyPath = AppPaths.TicketKeyFile;

                // Frontend updates, when a release server is configured, live beside the database.
                o.DataDirectory = AppPaths.WebAppDirectory;
            },
            // The operator owns the switch, from the window or the tray. Auto-start is honoured
            // after startup rather than during registration, so a bind failure is visible.
            autoStart: false
        );

        // One call for all three heads. Shiny picks the mechanism from the TFM the head resolved -
        // the HKCU Run key on Windows, an SMAppService login item on macOS, an XDG autostart entry
        // on the plain net10.0 Linux head - so there is nothing per-head to write or keep in step.
        builder.AddStartupService(o =>
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

        builder.Services.AddTransient<ConsoleWindowPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder;
    }

    /// <summary>
    /// Uploads are scanned once they are whole and before they are published. Which engine does the
    /// scanning is a property of the machine, not of the app: the Windows head calls AMSI, which is
    /// already there, and everywhere else it is a clamd the operator chose to run - so it is an
    /// environment variable rather than a setting, read where the server is started rather than from
    /// a database an administrator could point at a daemon that does not exist.
    ///
    ///   FILESERVE_CLAMD=1                on for a clamd at 127.0.0.1:3310
    ///   FILESERVE_CLAMD=clamav.lan:3310  on for a clamd elsewhere
    ///   FILESERVE_CLAMD_PATHSCAN=1       let clamd open the file itself instead of streaming it
    ///   FILESERVE_SCAN_STRICT=1          refuse anything that could not be scanned
    /// </summary>
    static void ConfigureScanning(IServiceCollection services)
    {
        var strict = IsOn(Environment.GetEnvironmentVariable("FILESERVE_SCAN_STRICT"));
        services.AddFileScanning(o => o.RejectUnscannable = strict);

        var clamd = Environment.GetEnvironmentVariable("FILESERVE_CLAMD");
        if (String.IsNullOrWhiteSpace(clamd))
            return;

        var host = "127.0.0.1";
        var port = 3310;

        if (!IsOn(clamd))
        {
            var parts = clamd.Split(':', 2);
            host = parts[0].Trim();
            if (parts.Length == 2 && Int32.TryParse(parts[1], out var parsed))
                port = parsed;
        }

        services.AddClamAvScanner(o =>
        {
            o.Host = host;
            o.Port = port;
            o.ScanByPath = IsOn(Environment.GetEnvironmentVariable("FILESERVE_CLAMD_PATHSCAN"));
        });
    }

    static bool IsOn(string? value)
        => value is not null && (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase));

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
