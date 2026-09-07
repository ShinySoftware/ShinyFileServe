using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Files;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;
using CoreUser = ShinyFileServe.Core.Documents.User;

namespace ShinyFileServe.Server.Endpoints;

[Route("/api/files")]
[Authorize]
public class FileEndpoints(
    ICurrentUserResolver current,
    IAccessResolver access,
    IFolderManager folders,
    IFileService files,
    IQuotaTracker quotas,
    IActivityLogger activity
)
{
    // -----------------------------------------------------------------------
    // List
    // -----------------------------------------------------------------------

    [Get]
    [Produces(200, typeof(DirectoryListing))]
    [Produces(403)]
    [Produces(404)]
    public async Task<IActionResult> List(string folderId, HttpContext ctx, string? path = null, CancellationToken ct = default)
    {
        var (user, folder, denied) = await this.Authorize(ctx, folderId, FolderPermissions.List, path, ct);
        if (denied is not null)
            return denied;

        try
        {
            var entries = files.List(folder!, path);

            // Names and notes live beside the share, not on disk, so they are folded in here rather
            // than by the file service - which knows nothing but the filesystem.
            var annotations = await folders.GetAnnotations(folderId, ct);
            if (annotations.Count > 0)
            {
                entries = entries
                    .Select(e => annotations.TryGetValue(e.Path, out var note)
                        ? e with { DisplayName = note.DisplayName, Description = note.Description }
                        : e)
                    .ToList();
            }

            await activity.Log(
                ActivityAction.List, user!.Id, user.Username, success: true,
                folder!.Folder.Id, folder.Folder.Name, path, context: current.GetActivityContext(ctx), ct: ct
            );
            return new OkObjectResult(new DirectoryListing(folderId, Normalize(path), entries));
        }
        catch (FileAccessException ex)
        {
            return await this.Failed(ctx, user!, folder!, ActivityAction.List, path, ex, ct);
        }
    }

    // -----------------------------------------------------------------------
    // Download
    // -----------------------------------------------------------------------

    [Get("/download")]
    [Produces(200)]
    [Produces(403)]
    [Produces(404)]
    public async Task<IActionResult> Download(string folderId, string path, HttpContext ctx, CancellationToken ct = default)
    {
        var (user, folder, denied) = await this.Authorize(ctx, folderId, FolderPermissions.DownloadFile, path, ct);
        if (denied is not null)
            return denied;

        try
        {
            var info = files.GetFile(folder!, path);

            if (folder!.DownloadWindow != QuotaWindow.None && folder.DownloadLimit is { } limit)
            {
                var usage = await quotas.GetUsage(user!.Id, folderId, folder.DownloadWindow, ct);
                if (usage.DownloadedBytes + info.Length > limit)
                {
                    await activity.Log(
                        ActivityAction.Denied, user.Id, user.Username, success: false,
                        folderId, folder.Folder.Name, path, info.Length,
                        "Download quota exceeded", current.GetActivityContext(ctx), ct
                    );
                    return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
                }
            }

            // The whole file counts against the quota the moment it is authorised, even for a range
            // request. Charging per-range would let a client fetch a file in pieces for free.
            await quotas.RecordDownload(user!.Id, folderId, folder.DownloadWindow, info.Length, ct);
            await activity.Log(
                ActivityAction.Download, user.Id, user.Username, success: true,
                folderId, folder.Folder.Name, path, info.Length,
                context: current.GetActivityContext(ctx), ct: ct
            );

            return FileDownloadResult.FromFile(info.FullName, downloadName: info.Name);
        }
        catch (FileAccessException ex)
        {
            return await this.Failed(ctx, user!, folder!, ActivityAction.Download, path, ex, ct);
        }
    }

    // -----------------------------------------------------------------------
    // Upload
    // -----------------------------------------------------------------------

    /// <summary>
    /// Streaming multipart upload. <c>path</c> is the directory the file lands in; the filename comes
    /// from the part itself, sanitised. Several files may ride in one request.
    /// </summary>
    [Post("/upload")]
    [Produces(200, typeof(IReadOnlyList<FileEntryInfo>))]
    [Produces(403)]
    [Produces(413)]
    public async Task<IActionResult> Upload(string folderId, HttpContext ctx, string? path = null, bool overwrite = false, CancellationToken ct = default)
    {
        var (user, folder, denied) = await this.Authorize(ctx, folderId, FolderPermissions.CreateFile, path, ct);
        if (denied is not null)
            return denied;

        if (!(ctx.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) ?? false))
            return new BadRequestObjectResult("Expected a multipart/form-data body");

        var uploaded = new List<FileEntryInfo>();
        var activityContext = current.GetActivityContext(ctx);

        try
        {
            await foreach (var part in ctx.Request.ReadMultipartAsync(ct))
            {
                if (!part.IsFile)
                    continue;

                var name = part.SafeFileName();
                if (String.IsNullOrWhiteSpace(name))
                    continue;

                var target = Combine(path, name);

                // Cheapest checks first: a per-file cap and the remaining quota are both knowable
                // before a single byte is written, so a doomed upload is refused rather than absorbed.
                var declared = ctx.Request.ContentLength ?? 0;
                if (folder!.MaxUploadFileSizeBytes is { } maxFile && declared > maxFile)
                {
                    await activity.Log(
                        ActivityAction.Denied, user!.Id, user.Username, false, folderId, folder.Folder.Name,
                        target, declared, $"Exceeds the {maxFile} byte per-file limit", activityContext, ct
                    );
                    return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
                }

                long remaining = long.MaxValue;
                if (folder.UploadWindow != QuotaWindow.None && folder.UploadLimit is { } limit)
                {
                    var usage = await quotas.GetUsage(user!.Id, folderId, folder.UploadWindow, ct);
                    remaining = limit - usage.UploadedBytes;
                    if (remaining <= 0)
                    {
                        await activity.Log(
                            ActivityAction.Denied, user.Id, user.Username, false, folderId, folder.Folder.Name,
                            target, 0, "Upload quota exhausted", activityContext, ct
                        );
                        return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
                    }
                }

                var written = await files.Save(folder, target, part.Body, overwrite, ct);

                if (written > remaining || (folder.MaxUploadFileSizeBytes is { } cap && written > cap))
                {
                    // the real size only became knowable once the stream ran out; undo rather than
                    // let an over-quota file sit on disk
                    try { files.Delete(folder, target); } catch (FileAccessException) { }

                    await activity.Log(
                        ActivityAction.Denied, user!.Id, user.Username, false, folderId, folder.Folder.Name,
                        target, written, "Upload exceeded the quota or per-file limit", activityContext, ct
                    );
                    return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
                }

                await quotas.RecordUpload(user!.Id, folderId, folder.UploadWindow, written, ct);
                await activity.Log(
                    ActivityAction.Upload, user.Id, user.Username, true, folderId, folder.Folder.Name,
                    target, written, context: activityContext, ct: ct
                );

                var info = files.GetFile(folder, target);
                uploaded.Add(new FileEntryInfo(info.Name, target, false, info.Length, info.LastWriteTimeUtc));
            }
        }
        catch (FileAccessException ex)
        {
            return await this.Failed(ctx, user!, folder!, ActivityAction.Upload, path, ex, ct);
        }

        if (uploaded.Count == 0)
            return new BadRequestObjectResult("No files were present in the request");

        return new OkObjectResult((IReadOnlyList<FileEntryInfo>)uploaded);
    }

    // -----------------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------------

    [Delete]
    [Produces(204)]
    [Produces(403)]
    [Produces(404)]
    public async Task<IActionResult> Delete(string folderId, string path, HttpContext ctx, CancellationToken ct = default)
    {
        var (user, folder, denied) = await this.Authorize(ctx, folderId, FolderPermissions.DeleteFile, path, ct);
        if (denied is not null)
            return denied;

        try
        {
            var info = files.GetFile(folder!, path);
            var size = info.Length;
            files.Delete(folder!, path);

            await activity.Log(
                ActivityAction.Delete, user!.Id, user.Username, true, folderId, folder!.Folder.Name,
                path, size, context: current.GetActivityContext(ctx), ct: ct
            );
            return new NoContentResult();
        }
        catch (FileAccessException ex)
        {
            return await this.Failed(ctx, user!, folder!, ActivityAction.Delete, path, ex, ct);
        }
    }

    // -----------------------------------------------------------------------
    // Create directory
    // -----------------------------------------------------------------------

    [Post("/directory")]
    [Produces(200, typeof(FileEntryInfo))]
    [Produces(403)]
    public async Task<IActionResult> CreateDirectory(CreateDirectoryRequest request, HttpContext ctx, CancellationToken ct = default)
    {
        var (user, folder, denied) = await this.Authorize(ctx, request.FolderId, FolderPermissions.CreateDirectory, request.Path, ct);
        if (denied is not null)
            return denied;

        try
        {
            var created = files.CreateDirectory(folder!, request.Path, request.Name);
            await activity.Log(
                ActivityAction.CreateDirectory, user!.Id, user.Username, true, request.FolderId,
                folder!.Folder.Name, created.Path, context: current.GetActivityContext(ctx), ct: ct
            );
            return new OkObjectResult(created);
        }
        catch (FileAccessException ex)
        {
            return await this.Failed(ctx, user!, folder!, ActivityAction.CreateDirectory, request.Path, ex, ct);
        }
    }

    // -----------------------------------------------------------------------
    // Shared pre-flight
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the caller, their access to the folder, and whether the requested permission is held.
    /// A refusal is logged here so the audit trail records the attempt, not just the successes.
    /// </summary>
    async Task<(CoreUser? User, FolderAccess? Folder, IActionResult? Denied)> Authorize(
        HttpContext ctx, string folderId, FolderPermissions required, string? path, CancellationToken ct)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return (null, null, new UnauthorizedResult());

        var folder = await access.GetAccess(user, folderId, ct);
        if (folder is null)
        {
            await activity.Log(
                ActivityAction.Denied, user.Id, user.Username, false, folderId, null, path,
                detail: "No access to folder", context: current.GetActivityContext(ctx), ct: ct
            );
            // 404 rather than 403 - a user with no grant should not learn the folder exists
            return (user, null, new NotFoundResult());
        }

        if (!folder.Permissions.Has(required))
        {
            await activity.Log(
                ActivityAction.Denied, user.Id, user.Username, false, folderId, folder.Folder.Name, path,
                detail: $"Missing permission: {required.ToDisplay()}", context: current.GetActivityContext(ctx), ct: ct
            );
            return (user, folder, new ForbidResult());
        }

        return (user, folder, null);
    }

    async Task<IActionResult> Failed(
        HttpContext ctx, CoreUser user, FolderAccess folder, ActivityAction action,
        string? path, FileAccessException ex, CancellationToken ct)
    {
        await activity.Log(
            action, user.Id, user.Username, false, folder.Folder.Id, folder.Folder.Name, path,
            detail: ex.Message, context: current.GetActivityContext(ctx), ct: ct
        );
        return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? new NotFoundObjectResult(ex.Message)
            : new BadRequestObjectResult(ex.Message);
    }

    static string Normalize(string? path)
        => String.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Trim('/');

    static string Combine(string? parent, string name)
    {
        var prefix = Normalize(parent);
        return String.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
    }
}
