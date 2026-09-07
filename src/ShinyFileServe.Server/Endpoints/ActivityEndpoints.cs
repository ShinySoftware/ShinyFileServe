using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

[Route("/api/activity")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class ActivityEndpoints(IActivityLogger activity, IHostNameResolver hosts)
{
    /// <summary>The audit trail, newest first. Admin-only - it names every user and every path.</summary>
    [Get]
    [Produces(200, typeof(PagedResponse<ActivityEntryInfo>))]
    [Produces(403)]
    public async Task<IActionResult> Query(
        HttpContext ctx,
        string? userId = null,
        string? folderId = null,
        ActivityAction? action = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await activity.Query(
            new ActivityQuery(userId, folderId, action, fromUtc, toUtc, page, pageSize),
            ct
        );

        // Names for the addresses on this page only, from a bounded cache and a short budget. A page
        // that gets none back reads exactly as it did before this existed. Only an administrator can
        // reach here, which is what keeps the pattern of who is being looked up off the network.
        var names = await hosts.Resolve(result.Items.Select(x => x.IpAddress ?? ""), ct);
        if (names.Count > 0)
        {
            var items = result.Items
                .Select(x => x.IpAddress is { } ip && names.TryGetValue(ip, out var name) ? x with { HostName = name } : x)
                .ToList();

            result = result with { Items = items };
        }

        return new OkObjectResult(result);
    }
}
