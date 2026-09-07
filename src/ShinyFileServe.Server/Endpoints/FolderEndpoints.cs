using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

[Route("/api/folders")]
[Authorize]
public class FolderEndpoints(ICurrentUserResolver current, IAccessResolver access)
{
    /// <summary>
    /// Every share the caller can see, with their effective permissions and live quota consumption.
    /// This is what fills the tree's roots and the information bar.
    /// </summary>
    [Get]
    [Produces(200, typeof(IReadOnlyList<FolderInfo>))]
    public async Task<IActionResult> List(HttpContext ctx, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        var folders = await access.GetAccessibleFolders(user, ct);

        var results = new List<FolderInfo>(folders.Count);
        foreach (var folder in folders)
            results.Add(await access.ToFolderInfo(user, folder, ct));

        return new OkObjectResult(results);
    }

    /// <summary>One folder's rights and quota, refreshed after an upload or download moves the meter.</summary>
    [Get("/{id}")]
    [Produces(200, typeof(FolderInfo))]
    [Produces(404)]
    public async Task<IActionResult> Get(string id, HttpContext ctx, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        var folder = await access.GetAccess(user, id, ct);
        if (folder is null || !folder.Permissions.Has(FolderPermissions.List))
            return new NotFoundResult();

        return new OkObjectResult(await access.ToFolderInfo(user, folder, ct));
    }
}
