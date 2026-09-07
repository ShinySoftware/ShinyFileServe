using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
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
    IActivityLogger activity
)
{
    // -- users --------------------------------------------------------------

    [Get("/user-details")]
    [Produces(200, typeof(IReadOnlyList<UserDetail>))]
    public async Task<IActionResult> ListUsers(CancellationToken ct)
    {
        var all = await users.GetUsers(ct);
        var roles = await users.GetRoles(ct);
        return new OkObjectResult(all.Select(u => ToDetail(u, roles)).ToList());
    }

    [Post("/users")]
    [Produces(200, typeof(UserDetail))]
    [Produces(400)]
    public async Task<IActionResult> SaveUser(SaveUserRequest request, HttpContext ctx, CancellationToken ct)
    {
        if (String.IsNullOrWhiteSpace(request.Username))
            return new BadRequestObjectResult("A username is required.");

        var roles = await users.GetRoles(ct);
        User user;

        // Checked here so a bad password is one 400 rather than a half-saved account: CreateUser
        // would throw on it anyway, but only after the name and groups had already been accepted.
        if (request.Password is { Length: > 0 } candidate)
        {
            if (passwords.Validate(candidate, [request.Username, request.DisplayName]) is { Ok: false } invalid)
                return new BadRequestObjectResult(invalid.Message);
        }
        else if (String.IsNullOrEmpty(request.Id))
        {
            return new BadRequestObjectResult("A new account needs a password.");
        }

        if (String.IsNullOrEmpty(request.Id))
        {
            try
            {
                user = await users.CreateUser(request.Username, request.Password!, request.DisplayName, request.IsAdmin, ct);
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
        user.IsActive = request.IsActive;
        user.RoleIds = request.RoleIds.Where(id => roles.Any(r => r.Id == id)).ToList();

        // The protected account keeps its admin and its enabled state whatever was sent; SaveUser
        // enforces it, and re-reading afterwards is what makes the portal show the truth.
        await users.SaveUser(user, ct);

        if (request.Password is { Length: > 0 })
            await users.SetPassword(user.Id, request.Password, ct);

        await this.Log(ctx, $"Saved the account '{user.Username}'", ct);
        return new OkObjectResult(ToDetail((await users.GetUser(user.Id, ct))!, roles));
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
                all.Count(u => u.RoleIds.Contains(r.Id))
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
        await users.SaveRole(role, ct);

        await this.Log(ctx, $"Saved the group '{role.Name}'", ct);

        var members = await users.GetUsers(ct);
        return new OkObjectResult(new RoleInfo(
            role.Id, role.Name, role.Description, role.IsAdmin, role.IsProtected,
            members.Count(u => u.RoleIds.Contains(role.Id))
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

    static UserDetail ToDetail(User user, IReadOnlyList<Role> roles) => new(
        user.Id,
        user.Username,
        user.DisplayName,
        user.Email,
        user.IsAdmin,
        user.IsActive,
        user.IsProtected,
        user.RoleIds,
        roles.Where(r => user.RoleIds.Contains(r.Id)).Select(r => r.Name).ToList(),
        user.CreatedUtc,
        user.LastLoginUtc
    );

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
