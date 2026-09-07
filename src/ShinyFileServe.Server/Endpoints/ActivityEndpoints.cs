using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

[Route("/api/activity")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class ActivityEndpoints(IActivityLogger activity)
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
        return new OkObjectResult(result);
    }
}
