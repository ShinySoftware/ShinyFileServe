using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;
using ShinyFileServe.Core.Services;

namespace ShinyFileServe.Server.Security;

/// <summary>
/// Signs in a request that came from the machine running the server, without a password.
/// </summary>
/// <remarks>
/// <para>
/// The desktop app is a window onto its own server. Whoever is sitting at that machine already
/// controls the process, the database and the files it serves - asking them for a password to see
/// their own screen protects nothing and just adds a step.
/// </para>
/// <para>
/// The whole trust rests on two facts about the connection, both checked every request. It must
/// come from a loopback address, and it must not have arrived through a tunnel: pinggy forwards
/// into loopback, so the address alone would hand the entire internet an administrator the moment
/// the tunnel was opened. There is no header involved by design - a header is something a client
/// can claim, and this decides who somebody is.
/// </para>
/// </remarks>
public class LoopbackSignInMiddleware(ILoopbackIdentity identity) : IHttpMiddleware
{
    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.User.Identity is not { IsAuthenticated: true } && IsLocal(context))
        {
            // Signing out on loopback is meaningless - the next request signs straight back in -
            // so the one route that would fight this is left alone.
            var path = context.Request.Path;
            if (!path.EndsWith("/api/auth/logout", StringComparison.OrdinalIgnoreCase))
            {
                if (await identity.Resolve(context.RequestAborted) is { } principal)
                {
                    context.User = principal;

                    // Issued as a cookie too, so the browser carries it and every later request -
                    // including ones this middleware chooses not to touch - is already signed in.
                    await context.SignInAsync(principal, persistent: false);
                }
            }
        }

        await next(context);
    }

    internal static bool IsLocal(HttpContext context)
    {
        // A tunnelled connection terminates on loopback at this end. Address alone is not enough.
        if (context.Connection.IsTunneled)
            return false;

        var remote = context.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote);
    }
}

/// <summary>Decides which account a loopback caller is.</summary>
public interface ILoopbackIdentity
{
    Task<ClaimsPrincipal?> Resolve(CancellationToken ct = default);
}

public class LoopbackIdentity(IUserManager users) : ILoopbackIdentity
{
    public async Task<ClaimsPrincipal?> Resolve(CancellationToken ct = default)
    {
        var all = await users.GetUsers(ct);

        // The account created at setup, then any other active administrator. Before setup there is
        // nobody to be, and the frontend sends the caller to the setup page instead.
        var user = all.FirstOrDefault(u => u is { IsProtected: true, IsActive: true })
            ?? all.FirstOrDefault(u => u is { IsAdmin: true, IsActive: true });

        if (user is null)
            return null;

        var roles = await users.GetRolesFor(user, ct);
        var principal = PrincipalFactory.Create(user, roles);

        // Marks the session so the portal can hide a Sign out button that would do nothing.
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(PrincipalFactory.LocalSessionClaim, "true"));
        return principal;
    }
}
