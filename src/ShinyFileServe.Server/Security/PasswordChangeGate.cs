using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using CoreUser = ShinyFileServe.Core.Documents.User;

namespace ShinyFileServe.Server.Security;

/// <summary>
/// Holds a session whose password has outlived the maximum age to the only two things it may still
/// do: set a new password, and sign out.
/// </summary>
/// <remarks>
/// <para>
/// Enforced here rather than in the portal because the portal is a client. A person who is being
/// asked to change their password still holds a perfectly valid cookie, and a browser is not the
/// only thing that can send it - so the refusal has to live where the data is, and every API route
/// but <c>/api/auth</c> and <c>/api/account</c> is behind it.
/// </para>
/// <para>
/// 428 rather than 403: the request was not forbidden to this account, it is missing a precondition
/// the account can satisfy itself. Nothing else in this server answers 428, so the frontend can act
/// on the status alone, and the body says in words what to do about it.
/// </para>
/// </remarks>
public class PasswordChangeGateMiddleware(ICurrentUserResolver current, IPasswordPolicy passwords) : IHttpMiddleware
{
    public const string Message = "Your password has expired. Set a new one before you carry on.";

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (IsGated(context.Request.Path) && await this.MustChange(context))
        {
            context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
            await context.Response.WriteTextAsync(Message, "text/plain; charset=utf-8", context.RequestAborted);
            return;
        }

        await next(context);
    }

    /// <summary>
    /// Everything with data behind it. The portal's own files are not gated - it has to load to be
    /// able to show anybody the password box - and neither is the auth surface, which is how a
    /// session in this state signs out, nor the account surface, which is how it gets out of it.
    /// </summary>
    static bool IsGated(string path)
        => path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/account/", StringComparison.OrdinalIgnoreCase);

    async ValueTask<bool> MustChange(HttpContext context)
    {
        // The overwhelmingly common case is a server that expires nothing, and this runs on every
        // request. Asking the settings first keeps it free there.
        if (passwords.Current.MaximumAgeDays <= 0)
            return false;

        var user = await current.Resolve(context, context.RequestAborted);
        return user is not null && Applies(context, user, passwords);
    }

    /// <summary>
    /// Whether this session is the one being asked to change its password. Shared with the endpoints
    /// that report it, so what the gate refuses and what <c>CurrentUser.MustChangePassword</c> says
    /// are the same judgement made once.
    /// </summary>
    /// <remarks>
    /// The loopback session is exempt. It is granted for sitting at the machine that runs the server
    /// rather than for typing a password, so there is no password of its own to be stale - and
    /// demanding one would put the desktop app, whose only window is this portal, behind a form for
    /// a credential nobody entered. Whoever is at that keyboard already owns the process, the
    /// database and the files; rotating a password protects nothing against them.
    /// </remarks>
    public static bool Applies(HttpContext context, CoreUser user, IPasswordPolicy passwords)
        => !context.User.HasClaim(PrincipalFactory.LocalSessionClaim, "true")
            && passwords.MustChangePassword(user);
}
