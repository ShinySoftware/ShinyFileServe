using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Server.Transfers;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// Users and groups, managed from the portal. The desktop app has no screens of its own - it is a
/// window onto this - so everything an operator can do lives behind these routes.
/// </summary>
[Route("/api/admin")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminUsersEndpoints(
    IUserManager users,
    IPasswordPolicy passwords,
    ICurrentUserResolver current,
    IActivityLogger activity,
    ITransferMonitor transfers,
    IQuotaTracker quotas,
    IAccessTokenManager tokens,
    TimeProvider time
)
{
    // -- users --------------------------------------------------------------

    [Get("/user-details")]
    [Produces(200, typeof(IReadOnlyList<UserDetail>))]
    public async Task<IActionResult> ListUsers(CancellationToken ct)
    {
        var all = await users.GetUsers(ct);
        var roles = await users.GetRoles(ct);
        var tokenCounts = (await tokens.GetAllTokens(ct)).GroupBy(t => t.UserId).ToDictionary(g => g.Key, g => g.Count());

        var details = new List<UserDetail>(all.Count);
        foreach (var user in all)
            details.Add(await this.ToDetail(user, roles, ct, tokenCounts.GetValueOrDefault(user.Id)));

        return new OkObjectResult(details);
    }

    [Post("/users")]
    [Produces(200, typeof(UserDetail))]
    [Produces(400)]
    public async Task<IActionResult> SaveUser(SaveUserRequest request, HttpContext ctx, CancellationToken ct)
    {
        if (String.IsNullOrWhiteSpace(request.Username))
            return new BadRequestObjectResult("A username is required.");

        // An account is either open to everybody or it has a password. Sending both is a request
        // that cannot be honoured either way round, so it is refused rather than resolved.
        if (request.IsAnonymous && request.Password is { Length: > 0 })
            return new BadRequestObjectResult("An account that signs in without a password cannot also be given one.");

        // A conflict rather than a bad request, because the same rule refused through a group
        // membership answers 409 from UserManager - one rule should not answer two ways.
        if (request.IsAnonymous && request.IsAdmin)
            return new ConflictObjectResult("An account that signs in without a password cannot be an administrator.");

        var roles = await users.GetRoles(ct);
        User user;

        // Checked here so a bad password is one 400 rather than a half-saved account: CreateUser
        // would throw on it anyway, but only after the name and groups had already been accepted.
        if (request.Password is { Length: > 0 } candidate)
        {
            if (passwords.Validate(candidate, [request.Username, request.DisplayName]) is { Ok: false } invalid)
                return new BadRequestObjectResult(invalid.Message);
        }
        else if (String.IsNullOrEmpty(request.Id) && !request.IsAnonymous)
        {
            return new BadRequestObjectResult("A new account needs a password.");
        }

        if (String.IsNullOrEmpty(request.Id))
        {
            try
            {
                user = request.IsAnonymous
                    ? await users.CreateAnonymousUser(request.Username, request.DisplayName, ct)
                    : await users.CreateUser(request.Username, request.Password!, request.DisplayName, request.IsAdmin, ct);
            }
            catch (ArgumentException ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return new ConflictObjectResult(ex.Message);
            }
        }
        else
        {
            user = await users.GetUser(request.Id, ct) ?? throw new BadHttpRequestException("No such user", 404);
        }

        user.Username = request.Username.Trim();
        user.DisplayName = String.IsNullOrWhiteSpace(request.DisplayName) ? user.Username : request.DisplayName.Trim();
        user.Email = String.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        user.IsAdmin = request.IsAdmin;
        user.ExpiresAtUtc = request.ExpiresAtUtc;
        user.IsAnonymous = request.IsAnonymous;
        user.MaxParallelDownloads = Cap(request.MaxParallelDownloads);
        user.MaxParallelUploads = Cap(request.MaxParallelUploads);
        (user.DownloadQuotaWindow, user.DownloadQuotaBytes) = Quota(request.DownloadQuotaWindow, request.DownloadQuotaBytes);
        (user.UploadQuotaWindow, user.UploadQuotaBytes) = Quota(request.UploadQuotaWindow, request.UploadQuotaBytes);
        user.RoleIds = request.RoleIds.Where(id => roles.Any(r => r.Id == id)).ToList();

        // Null leaves them alone, so an older client that knows nothing of them cannot reset them.
        if (request.NuGetAccess is { } nuget && Enum.IsDefined(nuget))
            user.NuGetAccess = nuget;

        if (request.NpmAccess is { } npm && Enum.IsDefined(npm))
            user.NpmAccess = npm;

        // The protected account keeps its admin and its perpetual life whatever was sent, and an
        // anonymous account is refused administration through a group here rather than in the pages.
        // Re-reading afterwards is what makes the portal show the truth.
        try
        {
            await users.SaveUser(user, ct);
        }
        catch (ProtectedAccountException ex)
        {
            return new ConflictObjectResult(ex.Message);
        }

        if (request.Password is { Length: > 0 })
            await users.SetPassword(user.Id, request.Password, ct);

        await this.Log(ctx, $"Saved the account '{user.Username}'", ct);
        return new OkObjectResult(await this.ToDetail(
            (await users.GetUser(user.Id, ct))!, roles, ct,
            (await tokens.GetTokens(user.Id, ct)).Count
        ));
    }

    /// <summary>
    /// A limit of nothing at all and a limit of zero say the same thing - an account allowed no
    /// transfers is an account with no access, which is what the grants are for - so both are
    /// stored as no limit rather than as a cap nobody could ever satisfy.
    /// </summary>
    static int? Cap(int? limit) => limit is not > 0 ? null : limit;

    /// <summary>
    /// A window with no byte count under it, and a byte count with no window over it, are both an
    /// operator having said half of something. Neither meters anything, so both are stored as the
    /// one state that reads back the same way it was meant: no quota.
    /// </summary>
    static (QuotaWindow Window, long? Bytes) Quota(QuotaWindow window, long? bytes)
        => window == QuotaWindow.None || bytes is not > 0 ? (QuotaWindow.None, null) : (window, bytes);

    /// <summary>
    /// Ends a lockout. Administrator-only, and deliberately the only way out of one: an account that
    /// could talk itself back in after enough tries would not be locked at all.
    /// </summary>
    [Post("/users/{id}/unlock")]
    [Produces(204)]
    [Produces(404)]
    public async Task<IActionResult> UnlockUser(string id, HttpContext ctx, CancellationToken ct)
    {
        var user = await users.GetUser(id, ct);
        if (user is null)
            return new NotFoundResult();

        await users.Unlock(id, ct);
        await this.Log(ctx, $"Unlocked the account '{user.Username}' after {user.FailedSignInCount} failed sign-ins", ct);
        return new NoContentResult();
    }

    [Delete("/users/{id}")]
    [Produces(204)]
    [Produces(409)]
    public async Task<IActionResult> DeleteUser(string id, HttpContext ctx, CancellationToken ct)
    {
        var user = await users.GetUser(id, ct);
        if (user is null)
            return new NoContentResult();

        // Deleting the account you are signed in as would lock you out of the machine you are
        // sitting at, which is the same class of mistake the protected flag exists to prevent.
        if (await current.Resolve(ctx, ct) is { } me && me.Id == id)
            return new ConflictObjectResult("You cannot delete the account you are signed in as.");

        try
        {
            await users.DeleteUser(id, ct);
        }
        catch (ProtectedAccountException ex)
        {
            return new ConflictObjectResult(ex.Message);
        }

        await this.Log(ctx, $"Deleted the account '{user.Username}'", ct);
        return new NoContentResult();
    }

    // -- groups -------------------------------------------------------------

    [Get("/roles")]
    [Produces(200, typeof(IReadOnlyList<RoleInfo>))]
    public async Task<IActionResult> ListRoles(CancellationToken ct)
    {
        var roles = await users.GetRoles(ct);
        var all = await users.GetUsers(ct);

        return new OkObjectResult(roles
            .Select(r => new RoleInfo(
                r.Id, r.Name, r.Description, r.IsAdmin, r.IsProtected,
                all.Count(u => u.RoleIds.Contains(r.Id)),
                r.PackagePermissions
            ))
            .ToList()
        );
    }

    [Post("/roles")]
    [Produces(200, typeof(RoleInfo))]
    [Produces(400)]
    public async Task<IActionResult> SaveRole(SaveRoleRequest request, HttpContext ctx, CancellationToken ct)
    {
        if (String.IsNullOrWhiteSpace(request.Name))
            return new BadRequestObjectResult("A group needs a name.");

        // The other way into the same mistake: an account anybody can sign in as, sitting in a group
        // that is about to be handed the server. Refused here as well as on the account itself.
        if (request.IsAdmin && !String.IsNullOrEmpty(request.Id))
        {
            var everyone = await users.GetUsers(ct);
            if (everyone.FirstOrDefault(u => u.IsAnonymous && u.RoleIds.Contains(request.Id)) is { } guest)
            {
                return new ConflictObjectResult(
                    $"'{guest.Username}' signs in without a password and is in this group, so the group cannot administer this server.");
            }
        }

        Role role;
        if (String.IsNullOrEmpty(request.Id))
        {
            role = await users.CreateRole(request.Name, request.Description, ct);
        }
        else
        {
            var existing = await users.GetRoles(ct);
            role = existing.FirstOrDefault(r => r.Id == request.Id)
                ?? throw new BadHttpRequestException("No such group", 404);
        }

        role.Name = request.Name.Trim();
        role.Description = String.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        role.IsAdmin = request.IsAdmin;

        // Null leaves them alone, so a caller that only flips the admin switch cannot wipe them.
        if (request.Packages is { } packages)
            role.PackagePermissions = packages & PackagePermissionsExtensions.All;

        await users.SaveRole(role, ct);

        await this.Log(ctx, $"Saved the group '{role.Name}'", ct);

        var members = await users.GetUsers(ct);
        return new OkObjectResult(new RoleInfo(
            role.Id, role.Name, role.Description, role.IsAdmin, role.IsProtected,
            members.Count(u => u.RoleIds.Contains(role.Id)),
            role.PackagePermissions
        ));
    }

    [Delete("/roles/{id}")]
    [Produces(204)]
    [Produces(409)]
    public async Task<IActionResult> DeleteRole(string id, HttpContext ctx, CancellationToken ct)
    {
        try
        {
            await users.DeleteRole(id, ct);
        }
        catch (ProtectedAccountException ex)
        {
            return new ConflictObjectResult(ex.Message);
        }

        await this.Log(ctx, "Deleted a group", ct);
        return new NoContentResult();
    }

    // -----------------------------------------------------------------------

    async Task<UserDetail> ToDetail(User user, IReadOnlyList<Role> roles, CancellationToken ct, int tokenCount)
    {
        var memberOf = roles.Where(r => user.RoleIds.Contains(r.Id)).ToList();

        return new(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Email,
            user.IsAdmin,
            user.ExpiresAtUtc,
            user.IsProtected,
            user.IsAnonymous,
            this.CapState(user.Id, user.MaxParallelDownloads, TransferDirection.Download),
            this.CapState(user.Id, user.MaxParallelUploads, TransferDirection.Upload),
            await this.AccountQuota(user, download: true, ct),
            await this.AccountQuota(user, download: false, ct),
            user.RoleIds,
            memberOf.Select(r => r.Name).ToList(),
            user.CreatedUtc,
            user.LastLoginUtc,
            user.LockedOutUtc,
            user.FailedSignInCount,
            user.PackagePermissions(memberOf),
            user.NuGetAccess,
            user.NpmAccess,
            AdminRules.GroupPackagePermissions(memberOf),
            tokenCount
        );
    }

    /// <summary>
    /// One account-wide quota with this period's spend read against it, so the People list can show
    /// the ceiling and how close somebody is to it in the same breath. An unmetered direction never
    /// touches the store - there would be nothing to compare the answer to.
    /// </summary>
    async Task<QuotaState> AccountQuota(User user, bool download, CancellationToken ct)
    {
        var window = download ? user.DownloadQuotaWindow : user.UploadQuotaWindow;
        var limit = download ? user.DownloadQuotaBytes : user.UploadQuotaBytes;

        if (window == QuotaWindow.None || limit is null)
            return QuotaState.Unlimited;

        var usage = await quotas.GetAccountUsage(user.Id, window, ct);
        var (_, _, end) = QuotaTracker.Period(window, time.GetUtcNow());
        return new QuotaState(window, limit, download ? usage.DownloadedBytes : usage.UploadedBytes, end);
    }

    /// <summary>
    /// The cap plus what is running against it right now, so the list can show one against the
    /// other. An uncapped account is not counted at all - the number would mean nothing next to a
    /// limit that does not exist, and the monitor would be walked once per account for it.
    /// </summary>
    TransferCapState CapState(string userId, int? limit, TransferDirection direction)
        => limit is null
            ? TransferCapState.Uncapped
            : new TransferCapState(limit, transfers.CountLive(userId, direction));

    async Task Log(HttpContext ctx, string detail, CancellationToken ct)
    {
        if (await current.Resolve(ctx, ct) is not { } me)
            return;

        await activity.Log(
            ActivityAction.Login, me.Id, me.Username, success: true,
            detail: detail, context: current.GetActivityContext(ctx), ct: ct
        );
    }
}
