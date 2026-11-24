using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Server.Transfers;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// Resumable uploads that started and have not finished - bytes on the server that are waiting for
/// the rest of themselves. An administrator can see how far each got and when it was last touched,
/// and throw away the ones nobody is coming back for rather than wait for the expiry sweep.
/// </summary>
[Route("/api/admin/uploads")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminUploadsEndpoints(
    ResumableUploads uploads,
    ICurrentUserResolver current,
    IActivityLogger activity
)
{
    [Get]
    [Produces(200, typeof(List<PartialUploadInfo>))]
    public async Task<IActionResult> List(CancellationToken ct)
        => new OkObjectResult(await uploads.ListUnfinished(ct));

    [Delete("/{id}")]
    [Produces(204)]
    public async Task<IActionResult> Delete(string id, HttpContext ctx, CancellationToken ct)
    {
        var target = (await uploads.ListUnfinished(ct)).FirstOrDefault(x => x.Id == id);
        if (!await uploads.Remove(id, ct))
            return new NoContentResult();

        if (await current.Resolve(ctx, ct) is { } me)
        {
            var whose = target?.DisplayName is { } name ? $" belonging to {name}" : "";
            await activity.Log(
                ActivityAction.Delete, me.Id, me.Username, success: true,
                folderId: target?.FolderId, folderName: target?.FolderName, path: target?.Path,
                bytes: target?.ReceivedBytes ?? 0,
                detail: $"Removed an unfinished upload{whose}",
                context: current.GetActivityContext(ctx), ct: ct
            );
        }
        return new NoContentResult();
    }
}
