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
    public async Task<IActionResult> Policy(CancellationToken ct)
    {
        var config = settings.Current;
        return new OkObjectResult(new SessionPolicy(
            await users.NeedsSetup(ct),
            config.AllowRememberMe,
            config.RememberMeDays,
            Math.Max(0, config.IdleLogoffMinutes)
        ));
    }

    /// <summary>
    /// Creates the first administrator and signs them in. Anonymous by necessity - there is nobody
    /// to authenticate as yet - and refused the moment any account exists, so it cannot be replayed
    /// by the next person to find the address.
    /// </summary>
    [Post("/setup")]
    [AllowAnonymous]
    [Produces(200, typeof(CurrentUser))]
    [Produces(400)]
    [Produces(409)]
    public async Task<IActionResult> Setup(SetupRequest request, HttpContext ctx, CancellationToken ct)
    {
        var context = current.GetActivityContext(ctx);

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
        var user = await users.Authenticate(request.Username, request.Password, ct);

        if (user is null)
        {
            await activity.Log(
                ActivityAction.LoginFailed, null, request.Username, success: false,
                detail: "Invalid credentials", context: context, ct: ct
            );
            // deliberately vague - never say whether it was the username or the password
            return new UnauthorizedResult();
        }

        var roles = await users.GetRolesFor(user, ct);
        var principal = PrincipalFactory.Create(user, roles);

        // A client can always ask to be remembered; whether it happens is the server's call.
        var remember = request.RememberMe && settings.Current.AllowRememberMe;
        await ctx.SignInAsync(principal, remember);

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
            ctx.User.HasClaim(PrincipalFactory.LocalSessionClaim, "true")
        ));
    }
}
