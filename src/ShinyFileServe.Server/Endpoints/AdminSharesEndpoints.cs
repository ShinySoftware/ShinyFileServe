using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// Shares and the grants on them. A grant is what a group may do inside one folder, and the quotas
/// that apply to its members.
/// </summary>
[Route("/api/admin/shares")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminSharesEndpoints(
    IFolderManager folders,
    IUserManager users,
    ICurrentUserResolver current,
    IActivityLogger activity
)
{
    [Get]
    [Produces(200, typeof(IReadOnlyList<ShareInfo>))]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var all = await folders.GetFolders(ct);
        var grants = await folders.GetGrants(ct);

        return new OkObjectResult(all.Select(f => ToInfo(f, grants.Count(g => g.FolderId == f.Id))).ToList());
    }

    [Get("/{id}")]
    [Produces(200, typeof(ShareDetail))]
    [Produces(404)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var folder = await folders.GetFolder(id, ct);
        if (folder is null)
            return new NotFoundResult();

        var grants = await folders.GetGrantsForFolder(id, ct);
        var roles = await users.GetRoles(ct);

        // Every group is returned, granted or not: the editor is a list of groups with rights
        // against them, and one that only showed the groups already granted would give no way to
        // add the others.
        var rows = roles
            .Select(role => grants.FirstOrDefault(g => g.RoleId == role.Id) is { } grant
                ? new GrantInfo(role.Id, role.Name, grant.Permissions, grant.DownloadQuotaWindow,
                    grant.DownloadQuotaBytes, grant.UploadQuotaWindow, grant.UploadQuotaBytes,
                    grant.MaxUploadFileSizeBytes)
                : new GrantInfo(role.Id, role.Name, FolderPermissions.None, QuotaWindow.None, null,
                    QuotaWindow.None, null, null))
            .ToList();

        return new OkObjectResult(new ShareDetail(ToInfo(folder, grants.Count), rows));
    }

    [Post]
    [Produces(200, typeof(ShareDetail))]
    [Produces(400)]
    public async Task<IActionResult> Save(SaveShareRequest request, HttpContext ctx, CancellationToken ct)
    {
        if (String.IsNullOrWhiteSpace(request.Name))
            return new BadRequestObjectResult("A share needs a name.");

        if (String.IsNullOrWhiteSpace(request.PhysicalPath))
            return new BadRequestObjectResult("A share needs a folder on this machine.");

        string full;
        try
        {
            full = Path.GetFullPath(request.PhysicalPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new BadRequestObjectResult("That is not a path this machine can resolve.");
        }

        if (!Directory.Exists(full))
        {
            if (!request.CreateDirectory)
                return new BadRequestObjectResult($"There is no folder at {full}.");

            try
            {
                Directory.CreateDirectory(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new BadRequestObjectResult($"That folder could not be created. {ex.Message}");
            }
        }

        ShareFolder folder;
        if (String.IsNullOrEmpty(request.Id))
        {
            folder = await folders.CreateFolder(request.Name, full, request.Description, ct);
        }
        else
        {
            folder = await folders.GetFolder(request.Id, ct) ?? throw new BadHttpRequestException("No such share", 404);
        }

        folder.Name = request.Name.Trim();
        folder.PhysicalPath = full;
        folder.Description = String.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        folder.IsEnabled = request.IsEnabled;
        await folders.SaveFolder(folder, ct);

        var existing = await folders.GetGrantsForFolder(folder.Id, ct);
        foreach (var row in request.Grants)
        {
            var prior = existing.FirstOrDefault(g => g.RoleId == row.RoleId);

            if (row.Permissions == FolderPermissions.None)
            {
                // Nothing ticked means no grant at all, rather than a grant that allows nothing.
                if (prior is not null)
                    await folders.DeleteGrant(prior.Id, ct);

                continue;
            }

            await folders.SetGrant(
                new FolderGrant
                {
                    Id = prior?.Id ?? "",
                    FolderId = folder.Id,
                    RoleId = row.RoleId,
                    Permissions = row.Permissions,

                    // A window with no number, or a number with no window, is not a quota - store
                    // neither, so the meter reads "No limit" rather than measuring against nothing.
                    DownloadQuotaWindow = row.DownloadBytes is null ? QuotaWindow.None : row.DownloadWindow,
                    DownloadQuotaBytes = row.DownloadWindow == QuotaWindow.None ? null : row.DownloadBytes,
                    UploadQuotaWindow = row.UploadBytes is null ? QuotaWindow.None : row.UploadWindow,
                    UploadQuotaBytes = row.UploadWindow == QuotaWindow.None ? null : row.UploadBytes,
                    MaxUploadFileSizeBytes = row.MaxUploadFileSizeBytes
                },
                ct
            );
        }

        await this.Log(ctx, $"Saved the share '{folder.Name}'", ct);
        return await this.Get(folder.Id, ct);
    }

    [Delete("/{id}")]
    [Produces(204)]
    public async Task<IActionResult> Delete(string id, HttpContext ctx, CancellationToken ct)
    {
        var folder = await folders.GetFolder(id, ct);
        if (folder is null)
            return new NoContentResult();

        // Only the share and its grants go. What is on disk is not this app's to delete.
        await folders.DeleteFolder(id, ct);
        await this.Log(ctx, $"Stopped sharing '{folder.Name}'", ct);
        return new NoContentResult();
    }

    static ShareInfo ToInfo(ShareFolder folder, int grantCount) => new(
        folder.Id,
        folder.Name,
        folder.PhysicalPath,
        folder.Description,
        folder.IsEnabled,
        Directory.Exists(folder.PhysicalPath),
        grantCount
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
