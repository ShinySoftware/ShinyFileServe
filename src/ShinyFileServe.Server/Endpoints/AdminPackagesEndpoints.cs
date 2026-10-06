using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// The package feeds from an administrator's side: every access token on the server and whose it is,
/// and the traffic log of who pulled and pushed what with them.
/// </summary>
[Route("/api/admin/packages")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminPackagesEndpoints(
    IAccessTokenManager tokens,
    IUserManager users,
    IPackageTrafficLog traffic,
    IActivityLogger activity,
    ICurrentUserResolver current,
    IHostNameResolver hosts
)
{
    /// <summary>Every token, or one account's. Never a secret - the server does not keep them.</summary>
    [Get("/tokens")]
    [Produces(200, typeof(IReadOnlyList<AdminAccessTokenInfo>))]
    public async Task<IActionResult> ListTokens(string? userId = null, CancellationToken ct = default)
    {
        var all = String.IsNullOrWhiteSpace(userId)
            ? await tokens.GetAllTokens(ct)
            : await tokens.GetTokens(userId, ct);

        var people = (await users.GetUsers(ct)).ToDictionary(u => u.Id);
        var roles = await users.GetRoles(ct);

        var result = new List<AdminAccessTokenInfo>(all.Count);
        foreach (var token in all)
        {
            people.TryGetValue(token.UserId, out var owner);
            var packages = owner is null
                ? PackagePermissions.None
                : owner.PackagePermissions(roles.Where(r => owner.RoleIds.Contains(r.Id)).ToList());

            result.Add(new AdminAccessTokenInfo(
                AccountEndpoints.ToInfo(token),
                token.UserId,
                owner?.Username ?? "(deleted)",
                owner?.DisplayName ?? "(deleted)",
                packages
            ));
        }

        return new OkObjectResult(result);
    }

    /// <summary>Revokes anybody's token. It stops working on the very next request.</summary>
    [Delete("/tokens/{id}")]
    [Produces(204)]
    [Produces(404)]
    public async Task<IActionResult> RevokeToken(string id, HttpContext ctx, CancellationToken ct)
    {
        var token = await tokens.RevokeAny(id, ct);
        if (token is null)
            return new NotFoundResult();

        if (await current.Resolve(ctx, ct) is { } me)
        {
            var owner = await users.GetUser(token.UserId, ct);
            await activity.Log(
                ActivityAction.Login, me.Id, me.Username, success: true,
                detail: $"Revoked the access token '{token.Name}' (…{token.Hint}) belonging to '{owner?.Username ?? token.UserId}'",
                context: current.GetActivityContext(ctx), ct: ct
            );
        }

        return new NoContentResult();
    }

    /// <summary>The package feed traffic log, newest first.</summary>
    [Get("/traffic")]
    [Produces(200, typeof(PagedResponse<PackageRequestInfo>))]
    public async Task<IActionResult> Traffic(
        string? userId = null,
        string? tokenId = null,
        PackageFeedKind? feed = null,
        PackageOperation? operation = null,
        string? packageId = null,
        bool refusedOnly = false,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await traffic.Query(
            new PackageTrafficQuery(userId, tokenId, feed, operation, packageId, refusedOnly, fromUtc, toUtc, page, pageSize),
            ct
        );

        // The same courtesy the activity log offers: a machine name beside the address where the
        // network will say, from the same bounded cache.
        var names = await hosts.Resolve(result.Items.Select(x => x.IpAddress ?? ""), ct);
        if (names.Count > 0)
        {
            result = result with
            {
                Items = result.Items
                    .Select(x => x.IpAddress is { } ip && names.TryGetValue(ip, out var name) ? x with { HostName = name } : x)
                    .ToList()
            };
        }

        return new OkObjectResult(result);
    }
}
