using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.Notifications;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Shared;

namespace ShinyFileServe.App.Services;

/// <summary>
/// Turns the activity log into desktop notifications, for whichever activities the operator has
/// asked to be told about. It listens rather than being called, so nothing on the request path has
/// to know notifications exist - and a notification that fails cannot fail a download.
/// </summary>
public class ActivityNotifier(
    IActivityLogger activity,
    ISettingsManager settings,
    TrayController tray,
    IServiceProvider services,
    ILogger<ActivityNotifier> logger
) : IDisposable
{
    bool accessChecked;
    bool accessGranted;

    /// <summary>
    /// Resolved rather than injected: AddNotifications() only exists on the platform builds of
    /// Shiny.Notifications, so whether a manager is present depends on which head is running.
    /// </summary>
    INotificationManager? Notifications => services.GetService(typeof(INotificationManager)) as INotificationManager;

    public void Start() => activity.Logged += this.OnLogged;

    void OnLogged(object? sender, ActivityLogEntry entry)
    {
        var config = settings.Current;
        if (!config.NotificationsEnabled || !config.NotifyOn.Contains(entry.Action))
            return;

        // Deliberately not awaited: this runs on the thread that just served a request, and a
        // notification daemon that is slow, missing, or asking for permission must not hold it up.
        _ = this.Send(entry);
    }

    async Task Send(ActivityLogEntry entry)
    {
        var title = Title(entry);
        var body = Body(entry);

        try
        {
            if (this.Notifications is not { } manager)
            {
                // No platform notification package on this head - the tray icon's own balloon is
                // the same idea through a different subsystem, and it is already here.
                tray.ShowNotification(title, body);
                return;
            }

            if (!this.accessChecked)
            {
                this.accessChecked = true;
                this.accessGranted = await manager.RequestAccess() == AccessState.Available;

                if (!this.accessGranted)
                    logger.LogWarning("Notification permission was not granted; falling back to the tray icon");
            }

            if (this.accessGranted)
                await manager.Send(title, body);
            else
                tray.ShowNotification(title, body);
        }
        catch (Exception ex)
        {
            // Desktop notification daemons are a best-effort dependency: absent on a bare Linux
            // session, refused on a locked-down Mac. Never let one take the server down.
            logger.LogWarning(ex, "Could not show a notification for {Action}", entry.Action);
        }
    }

    static string Title(ActivityLogEntry entry) => entry.Action switch
    {
        ActivityAction.Login => "Signed in",
        ActivityAction.LoginFailed => "Sign-in refused",
        ActivityAction.Logout => "Signed out",
        ActivityAction.Download => "File downloaded",
        ActivityAction.Upload => "File uploaded",
        ActivityAction.Delete => "File deleted",
        ActivityAction.CreateDirectory => "Folder created",
        ActivityAction.Denied => "Action refused",
        ActivityAction.PackagePublish => "Package published",
        ActivityAction.PackageRemove => "Package removed",
        _ => "FileServe"
    };

    static string Body(ActivityLogEntry entry)
    {
        var where = entry.FolderName is null
            ? null
            : String.IsNullOrEmpty(entry.Path) ? entry.FolderName : $"{entry.FolderName}/{entry.Path}";

        var parts = new List<string> { entry.Username };
        if (where is not null)
            parts.Add(where);
        if (entry.Detail is { Length: > 0 } detail)
            parts.Add(detail);
        else if (entry.IpAddress is { Length: > 0 } ip && where is null)
            parts.Add(ip);

        return String.Join(" · ", parts);
    }

    public void Dispose()
    {
        activity.Logged -= this.OnLogged;
        GC.SuppressFinalize(this);
    }
}
