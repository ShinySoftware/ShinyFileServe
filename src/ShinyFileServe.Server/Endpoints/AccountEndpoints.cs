using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// What a person may do to their own account. Nothing here takes an account id: every route acts on
/// whoever the cookie belongs to, so there is no parameter through which one account could reach
/// another, and no administrator check to get wrong.
/// </summary>
/// <remarks>
/// Deliberately outside the gate in <see cref="PasswordChangeGateMiddleware"/>. A session whose
/// password has expired can do nothing else, and this is the way out of that state - a change-password
/// route that the change-password rule refused would leave the person with only the sign-out button.
/// </remarks>
[Route("/api/account")]
[Authorize]
public class AccountEndpoints(
    IUserManager users,
    IPasswordPolicy passwords,
    ICurrentUserResolver current,
    IActivityLogger activity,
    IQuotaTracker quotas,
    IAccessTokenManager tokens,
    ISettingsManager settings,
    TimeProvider time
)
{
    /// <summary>Who the caller is, and how their password is doing. No credentials, ever.</summary>
    [Get("/profile")]
    [Produces(200, typeof(AccountProfile))]
    [Produces(401)]
    public async Task<IActionResult> Profile(HttpContext ctx, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        var roles = await users.GetRolesFor(user, ct);

        return new OkObjectResult(new AccountProfile(
            user.Id,
            user.Username,
            user.DisplayName,
            roles.Select(r => r.Name).ToList(),
            !user.IsAnonymous,
            user.IsAnonymous ? null : user.PasswordSetUtc,
            passwords.ExpiresAt(user),
            PasswordChangeGateMiddleware.Applies(ctx, user, passwords),
            PasswordPolicyMapping.ToInfo(passwords.Current),

            // Null rather than an unlimited meter: most accounts have no ceiling of their own, and a
            // pair of empty bars on every profile would be two answers to a question nobody asked.
            await this.AccountQuota(user, download: true, ct),
            await this.AccountQuota(user, download: false, ct),
            this.PackageAccess(user, roles)
        ));
    }

    PackageAccessInfo PackageAccess(Core.Documents.User user, IReadOnlyList<Core.Documents.Role> roles)
    {
        var feeds = settings.Current.PackageFeeds;
        return new PackageAccessInfo(
            feeds.NuGetEnabled,
            feeds.NpmEnabled,
            user.PackagePermissions(roles),
            feeds.Anonymous & PackagePermissionsExtensions.Reads,
            !user.IsAnonymous
        );
    }

    // -- access tokens ------------------------------------------------------

    /// <summary>The caller's own tokens. Never the secrets - those were shown once, when made.</summary>
    [Get("/tokens")]
    [Produces(200, typeof(IReadOnlyList<AccessTokenInfo>))]
    [Produces(401)]
    public async Task<IActionResult> Tokens(HttpContext ctx, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        return new OkObjectResult((await tokens.GetTokens(user.Id, ct)).Select(ToInfo).ToList());
    }

    /// <summary>
    /// Issues a token for <c>nuget.config</c> or <c>.npmrc</c>. The only response that ever carries
    /// the secret; the server keeps a hash and cannot show it again.
    /// </summary>
    [Post("/tokens")]
    [Produces(200, typeof(CreatedAccessToken))]
    [Produces(400)]
    [Produces(401)]
    [Produces(403)]
    public async Task<IActionResult> CreateToken(CreateAccessTokenRequest request, HttpContext ctx, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        if (user.IsAnonymous)
        {
            return new ObjectResult(
                "This account signs in without a password, so it cannot hold access tokens.",
                StatusCodes.Status403Forbidden
            );
        }

        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
            return new BadRequestObjectResult("Give the token a name of up to 100 characters, so you can tell it apart later.");

        if (request.ExpiresInDays is < 0 or > 3650)
            return new BadRequestObjectResult("A token may last between 1 and 3650 days. Leave it empty to never expire it.");

        DateTimeOffset? expires = request.ExpiresInDays is > 0 and var days ? time.GetUtcNow().AddDays(days) : null;
        var (token, secret) = await tokens.Create(user, name, expires, ct);

        await activity.Log(
            ActivityAction.Login, user.Id, user.Username, success: true,
            detail: $"Created the access token '{token.Name}'", context: current.GetActivityContext(ctx), ct: ct
        );

        return new OkObjectResult(new CreatedAccessToken(ToInfo(token), secret));
    }

    [Delete("/tokens/{id}")]
    [Produces(204)]
    [Produces(401)]
    [Produces(404)]
    public async Task<IActionResult> RevokeToken(string id, HttpContext ctx, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        if (!await tokens.Revoke(user.Id, id, ct))
            return new NotFoundResult();

        await activity.Log(
            ActivityAction.Login, user.Id, user.Username, success: true,
            detail: "Revoked an access token", context: current.GetActivityContext(ctx), ct: ct
        );
        return new NoContentResult();
    }

    static AccessTokenInfo ToInfo(Core.Documents.AccessToken token)
        => new(token.Id, token.Name, token.Hint, token.CreatedUtc, token.LastUsedUtc, token.ExpiresAtUtc);

    /// <summary>
    /// This account's own ceiling in one direction, with the period's spend read against it, or null
    /// when there is no ceiling to report.
    /// </summary>
    async Task<QuotaState?> AccountQuota(Core.Documents.User user, bool download, CancellationToken ct)
    {
        var window = download ? user.DownloadQuotaWindow : user.UploadQuotaWindow;
        var limit = download ? user.DownloadQuotaBytes : user.UploadQuotaBytes;

        if (window == QuotaWindow.None || limit is null)
            return null;

        var usage = await quotas.GetAccountUsage(user.Id, window, ct);
        var (_, _, end) = QuotaTracker.Period(window, time.GetUtcNow());
        return new QuotaState(window, limit, download ? usage.DownloadedBytes : usage.UploadedBytes, end);
    }

    /// <summary>
    /// Replaces the caller's own password. The current one has to be produced: a session left open
    /// on somebody else's screen is the ordinary way this endpoint gets misused, and asking for the
    /// password is what makes taking the account over cost more than walking past a desk.
    /// </summary>
    [Post("/password")]
    [Produces(204)]
    [Produces(400)]
    [Produces(401)]
    [Produces(403)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, HttpContext ctx, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        var context = current.GetActivityContext(ctx);

        // A body can arrive with either field missing however the record declares them.
        var currentPassword = request.CurrentPassword ?? "";
        var newPassword = request.NewPassword ?? "";

        // Nothing to replace, and nothing to check the request against.
        if (user.IsAnonymous)
        {
            return new ObjectResult(
                "This account signs in without a password, so there is none to change.",
                StatusCodes.Status403Forbidden
            );
        }

        // Already out of tries. Said plainly - the caller is holding this account's session, so the
        // reticence that governs the sign-in page has nothing left to protect here.
        if (user.IsLockedOut)
        {
            return new ObjectResult(
                "This account is locked. An administrator has to unlock it before its password can be changed.",
                StatusCodes.Status403Forbidden
            );
        }

        // The current-password check is a password guess like any other, so it is metered like one:
        // the same counter, the same threshold, and the same lockout at the end of it. Otherwise a
        // borrowed session would be an unlimited oracle on the password it was too polite to ask for.
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            await users.RegisterFailedSignIn(user, ct);
            await activity.Log(
                ActivityAction.LoginFailed, user.Id, user.Username, success: false,
                detail: user.IsLockedOut
                    ? $"Wrong current password on a password change - locked after {user.FailedSignInCount} attempts"
                    : "Wrong current password on a password change",
                context: context, ct: ct
            );

            return new ObjectResult("That is not your current password.", StatusCodes.Status401Unauthorized);
        }

        // Re-using the current password would satisfy the rules and change nothing, which is the one
        // outcome an expiry is meant to rule out.
        if (PasswordHasher.Verify(newPassword, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
            return new BadRequestObjectResult("The new password has to be different from the one you are replacing.");

        // The same policy every other path uses, asked the same way, so a browser that let something
        // through still meets the rule here.
        if (passwords.Validate(newPassword, [user.Username, user.DisplayName]) is { Ok: false } invalid)
            return new BadRequestObjectResult(invalid.Message);

        // Records the change and clears the failure count with it, which is what takes the session
        // back out of the must-change state - there is no separate flag to forget to reset.
        await users.SetPassword(user.Id, newPassword, ct);

        await activity.Log(
            ActivityAction.Login, user.Id, user.Username, success: true,
            detail: "Changed their own password", context: context, ct: ct
        );

        return new NoContentResult();
    }
}
