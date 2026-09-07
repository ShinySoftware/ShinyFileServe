using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;
using ShinyFileServe.Core.Services;

namespace ShinyFileServe.Server;

/// <summary>
/// Holds the live cookie options so the session rules can be changed while the server is running.
/// The alternative - baking the lifetime in at registration - would mean an operator could edit
/// "remembered sessions last N days" and have it do nothing until the app was restarted.
/// </summary>
public class SessionPolicyHolder
{
    internal CookieAuthenticationOptions? Cookie { get; set; }

    /// <summary>
    /// The outer bound on any cookie. A non-persistent sign-in still gets a session cookie the
    /// browser drops on close; this is the cap for the ones that are meant to survive.
    /// </summary>
    public void ApplyRememberWindow(int days)
    {
        if (this.Cookie is { } cookie)
            cookie.ExpireTimeSpan = TimeSpan.FromDays(Math.Clamp(days, 1, 3650));
    }

    /// <summary>
    /// Rejects a cookie that is cryptographically fine but no longer true: the account is gone, or
    /// its expiry has passed. Cookies carry their claims rather than a session id, so without this
    /// an expired user would keep working until their cookie happened to run out.
    /// </summary>
    internal static async ValueTask<bool> ValidateTicket(AuthenticationTicket ticket, HttpContext context)
    {
        var id = ticket.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (String.IsNullOrEmpty(id))
            return false;

        var users = context.RequestServices.GetService<IUserManager>();
        if (users is null)
            return true;

        var time = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var user = await users.GetUser(id, context.RequestAborted);
        return user is not null && !user.IsExpired(time.GetUtcNow());
    }
}
