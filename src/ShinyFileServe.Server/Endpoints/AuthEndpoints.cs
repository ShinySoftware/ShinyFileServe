using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

[Route("/api/auth")]
public class AuthEndpoints(
    IUserManager users,
    IActivityLogger activity,
    ISettingsManager settings,
    IPasswordPolicy passwords,
    ICurrentUserResolver current
)
{
    /// <summary>
    /// The session rules, before anyone has signed in - the sign-in page needs them to decide
    /// whether to offer "keep me signed in", and the app needs the idle timeout to run its timer.
    /// </summary>
    [Get("/policy")]
    [AllowAnonymous]
    [Produces(200, typeof(SessionPolicy))]
    public async Task<IActionResult> Policy(HttpContext ctx, CancellationToken ct)
    {
        var config = settings.Current;

        // Only the accounts that sign in without a password, and only their id and their name. The
        // manager filters the list rather than the endpoint, so there is one place to be wrong.
        var anonymous = (await users.GetAnonymousAccounts(ct))
            .Select(u => new AnonymousAccount(u.Id, u.DisplayName))
            .ToList();

        return new OkObjectResult(new SessionPolicy(
            await users.NeedsSetup(ct),
            LoopbackSignInMiddleware.IsLocal(ctx),
            config.AllowRememberMe,
            config.RememberMeDays,
            Math.Max(0, config.IdleLogoffMinutes),
            PasswordPolicyMapping.ToInfo(passwords.Current),
            anonymous
        ));
    }

    /// <summary>
    /// Creates the first administrator and signs them in. Anonymous by necessity - there is nobody
    /// to authenticate as yet - and hedged two ways so that cannot be abused: it is refused the
    /// moment any account exists, and it is refused from anywhere but the machine running the
    /// server. Without the second, whoever found the address first on the network would own it.
    /// </summary>
    [Post("/setup")]
    [AllowAnonymous]
    [Produces(200, typeof(CurrentUser))]
    [Produces(400)]
    [Produces(403)]
    [Produces(409)]
    public async Task<IActionResult> Setup(SetupRequest request, HttpContext ctx, CancellationToken ct)
    {
        var context = current.GetActivityContext(ctx);

        // A tunnelled connection terminates on loopback at this end, so the address alone is not
        // enough - IsLocal checks both.
        if (!LoopbackSignInMiddleware.IsLocal(ctx))
        {
            await activity.Log(
                ActivityAction.Denied, null, request.Username, success: false,
                detail: "Setup attempted from off this machine", context: context, ct: ct
            );
            return new ForbidResult();
        }

        try
        {
            var user = await users.CompleteSetup(request.Username, request.Password, request.DisplayName, ct);
            var roles = await users.GetRolesFor(user, ct);

            await ctx.SignInAsync(PrincipalFactory.Create(user, roles), persistent: false);
            await activity.Log(
                ActivityAction.Login, user.Id, user.Username, success: true,
                detail: "Created this server's administrator", context: context, ct: ct
            );

            return new OkObjectResult(PrincipalFactory.ToDto(user, roles));
        }
        catch (ArgumentException ex)
        {
            return new BadRequestObjectResult(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Someone got here first. Say so plainly rather than leaving a spinner.
            await activity.Log(
                ActivityAction.Denied, null, request.Username, success: false,
                detail: "Setup attempted after the server was already set up", context: context, ct: ct
            );
            return new ConflictObjectResult(ex.Message);
        }
    }

    /// <summary>Exchanges credentials for a session cookie.</summary>
    [Post("/login")]
    [AllowAnonymous]
    [Produces(200, typeof(CurrentUser))]
    [Produces(401)]
    public async Task<IActionResult> Login(LoginRequest request, HttpContext ctx, CancellationToken ct)
    {
        var context = current.GetActivityContext(ctx);
        var result = await users.Authenticate(request.Username, request.Password, ct);

        // Written whether or not the attempt was otherwise refused, and it is the only place the
        // lockout is named - the answer the caller gets is the same bare 401 either way.
        if (result.JustLocked is { } locked)
        {
            await activity.Log(
                ActivityAction.Denied, locked.Id, locked.Username, success: false,
                detail: $"Locked after {locked.FailedSignInCount} consecutive failed sign-ins",
                context: context, ct: ct
            );
        }

        if (result.User is not { } user)
        {
            // The expired and locked cases are named, and both are only ever reached by somebody
            // whose password was right, so they tell a person why they are stuck without telling a
            // stranger anything at all. Everything else is one bare 401.
            var detail = result.Failure switch
            {
                AuthenticationFailure.Expired => "The account has expired",
                AuthenticationFailure.LockedOut => "The account is locked",
                _ => "Invalid credentials"
            };

            await activity.Log(
                ActivityAction.LoginFailed, null, request.Username, success: false,
                detail: detail, context: context, ct: ct
            );

            return result.Failure switch
            {
                AuthenticationFailure.Expired => (IActionResult)new ObjectResult(
                    "This account has expired. Ask an administrator to renew it.",
                    StatusCodes.Status401Unauthorized
                ),
                AuthenticationFailure.LockedOut => new ObjectResult(
                    "This account is locked after too many failed sign-ins. Ask an administrator to unlock it.",
                    StatusCodes.Status401Unauthorized
                ),
                _ => new UnauthorizedResult()
            };
        }

        var roles = await users.GetRolesFor(user, ct);
        var principal = PrincipalFactory.Create(user, roles);

        // A client can always ask to be remembered; whether it happens is the server's call.
        var remember = request.RememberMe && settings.Current.AllowRememberMe;
        await ctx.SignInAsync(principal, remember);

        await activity.Log(ActivityAction.Login, user.Id, user.Username, success: true, context: context, ct: ct);

        // Signing in is allowed with a password past its age; doing anything afterwards is not, and
        // the shell reads this to put the change-password screen up straight away.
        return new OkObjectResult(PrincipalFactory.ToDto(
            user,
            roles,
            mustChangePassword: PasswordChangeGateMiddleware.Applies(ctx, user, passwords)
        ));
    }

    /// <summary>
    /// Signs the caller in as an account that carries no password. Unauthenticated by necessity -
    /// that is what the account is for - and narrow by design: it will only ever accept an account
    /// that is actually marked anonymous and has not expired, and it answers every other id the same
    /// way, so it cannot be used to find out which accounts exist.
    /// </summary>
    [Post("/anonymous")]
    [AllowAnonymous]
    [Produces(200, typeof(CurrentUser))]
    [Produces(401)]
    public async Task<IActionResult> Anonymous(AnonymousSignInRequest request, HttpContext ctx, CancellationToken ct)
    {
        var context = current.GetActivityContext(ctx);
        var result = await users.AuthenticateAnonymous(request.UserId ?? "", ct);

        if (result.User is not { } user)
        {
            await activity.Log(
                ActivityAction.LoginFailed, null, "(guest)", success: false,
                detail: "No such guest account", context: context, ct: ct
            );
            return new UnauthorizedResult();
        }

        var roles = await users.GetRolesFor(user, ct);

        // Never remembered. A shared account left signed in on a browser is the one thing an
        // anonymous account should not be able to do quietly.
        await ctx.SignInAsync(PrincipalFactory.Create(user, roles), persistent: false);

        await activity.Log(ActivityAction.Login, user.Id, user.Username, success: true, context: context, ct: ct);
        return new OkObjectResult(PrincipalFactory.ToDto(user, roles));
    }

    [Post("/logout")]
    public async Task<IActionResult> Logout(HttpContext ctx, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is not null)
        {
            await activity.Log(
                ActivityAction.Logout, user.Id, user.Username, success: true,
                context: current.GetActivityContext(ctx), ct: ct
            );
        }
        await ctx.SignOutAsync();
        return new NoContentResult();
    }

    /// <summary>Who the cookie belongs to. The frontend calls this on boot to decide login vs. app.</summary>
    [Get("/me")]
    [Produces(200, typeof(CurrentUser))]
    [Produces(401)]
    public async Task<IActionResult> Me(HttpContext ctx, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        var roles = await users.GetRolesFor(user, ct);
        return new OkObjectResult(PrincipalFactory.ToDto(
            user,
            roles,
            ctx.User.HasClaim(PrincipalFactory.LocalSessionClaim, "true"),
            PasswordChangeGateMiddleware.Applies(ctx, user, passwords)
        ));
    }
}
