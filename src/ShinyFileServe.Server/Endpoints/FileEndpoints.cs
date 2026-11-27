using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Files;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Server.Transfers;
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
    ITransferMonitor transfers,
    IUploadScanner scanner,
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
    [Produces(429)]
    public async Task<IActionResult> Download(string folderId, string path, HttpContext ctx, CancellationToken ct = default)
    {
        var (user, folder, denied) = await this.Authorize(ctx, folderId, FolderPermissions.DownloadFile, path, ct);
        if (denied is not null)
            return denied;

        try
        {
            var info = files.GetFile(folder!, path);
            var etag = ETagFor(info);

            // What this request will actually send: a range for a resume or a seek, nothing for a
            // 304 or a 416. That is what is checked against the quota and charged to it.
            var charge = DownloadCharge.For(ctx.Request, info.Length, etag, info.LastWriteTimeUtc);
            if (charge == 0)
                return Serve(info, etag, info.Name, ctx, null);

            if (folder!.DownloadWindow != QuotaWindow.None && folder.DownloadLimit is { } limit)
            {
                var usage = await quotas.GetUsage(user!.Id, folderId, folder.DownloadWindow, ct);
                if (usage.DownloadedBytes + charge > limit)
                {
                    await activity.Log(
                        ActivityAction.Denied, user.Id, user.Username, success: false,
                        folderId, folder.Folder.Name, path, info.Length,
                        "Download quota exceeded", current.GetActivityContext(ctx), ct
                    );
                    return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
                }
            }

            // And the account's own ceiling, which is measured across every share. Checked after the
            // folder's because the folder's is the one an administrator is likelier to have set, and
            // both refuse the same way - the order only decides which reason is logged.
            if (user!.DownloadQuotaWindow != QuotaWindow.None && user.DownloadQuotaBytes is { } accountLimit)
            {
                var usage = await quotas.GetAccountUsage(user.Id, user.DownloadQuotaWindow, ct);
                if (usage.DownloadedBytes + charge > accountLimit)
                {
                    await activity.Log(
                        ActivityAction.Denied, user.Id, user.Username, success: false,
                        folderId, folder.Folder.Name, path, info.Length,
                        "Account download quota exceeded", current.GetActivityContext(ctx), ct
                    );
                    return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
                }
            }

            // The account's own cap on how much of the server it may occupy at once, beside the
            // folder's cap on how many bytes it may move. Counted from what is actually running, so
            // a slot comes back the moment a transfer ends however it ended - there is nothing to
            // reconcile and nothing that can leak a slot the way a stored count could.
            //
            // The check is here rather than where the transfer is registered because that happens
            // inside the stream opener, by which time the 200 is already settled. Two downloads
            // starting in the same instant can therefore both pass a check for the last free slot;
            // one extra transfer for a moment is the right side of that trade, since the alternative
            // is refusing a download after its headers have gone out.
            if (user!.MaxParallelDownloads is { } maxParallel && transfers.CountLive(user.Id, TransferDirection.Download) >= maxParallel)
            {
                await activity.Log(
                    ActivityAction.Denied, user.Id, user.Username, success: false,
                    folderId, folder.Folder.Name, path, info.Length,
                    "Parallel download limit reached", current.GetActivityContext(ctx), ct
                );
                return new ObjectResult(
                    $"You already have {maxParallel} {(maxParallel == 1 ? "download" : "downloads")} running. Wait for one to finish and try again.",
                    StatusCodes.Status429TooManyRequests
                );
            }

            // Charged up front, for the bytes this request will send. A range costs its own length,
            // so a file fetched in pieces still costs every byte once, and a resume costs only the rest.
            await quotas.RecordDownload(user.Id, folderId, folder.DownloadWindow, charge, ct);
            await quotas.RecordAccountDownload(user.Id, user.DownloadQuotaWindow, charge, ct);
            await activity.Log(
                ActivityAction.Download, user.Id, user.Username, success: true,
                folderId, folder.Folder.Name, path, charge,
                context: current.GetActivityContext(ctx), ct: ct
            );

            var me = user!;
            return Serve(info, etag, info.Name, ctx, () => transfers.Begin(
                TransferDirection.Download, me.Id, me.Username, me.DisplayName,
                folderId, folder!.Folder.Name, Normalize(path), info.Length
            ));
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
    /// No body limit: the parts stream straight to disk, and the share's per-file cap and the
    /// quotas below are what decide how much is too much.
    /// </summary>
    [Post("/upload")]
    [DisableRequestSizeLimit]
    [Produces(200, typeof(IReadOnlyList<FileEntryInfo>))]
    [Produces(403)]
    [Produces(413)]
    [Produces(422)]
    [Produces(429)]
    [Produces(503)]
    public async Task<IActionResult> Upload(string folderId, HttpContext ctx, string? path = null, bool overwrite = false, CancellationToken ct = default)
    {
        var (user, folder, denied) = await this.Authorize(ctx, folderId, FolderPermissions.CreateFile, path, ct);
        if (denied is not null)
            return denied;

        if (!(ctx.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) ?? false))
            return new BadRequestObjectResult("Expected a multipart/form-data body");

        var uploaded = new List<FileEntryInfo>();
        var activityContext = current.GetActivityContext(ctx);
        var conflict = UploadRules.Conflict(folder!, overwrite);

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
                // Only the part's own length counts. The request's length is every file in the batch
                // plus the multipart framing, so checking that against a per-file cap refused a batch
                // of small files whose total happened to pass it. Browsers do not send a length per
                // part, so usually this is unknown and the limit is enforced as the bytes arrive.
                var declared = PartLength(part) ?? 0;
                if (folder!.MaxUploadFileSizeBytes is { } maxFile && declared > maxFile)
                {
                    await activity.Log(
                        ActivityAction.Denied, user!.Id, user.Username, false, folderId, folder.Folder.Name,
                        target, declared, $"Exceeds the {maxFile} byte per-file limit", activityContext, ct
                    );
                    return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
                }

                // The account's cap on how many uploads it may have running at once. Files inside one
                // request are read one after another, so this refuses a second request while the
                // first is still arriving rather than the second file of a batch.
                if (user!.MaxParallelUploads is { } maxParallel && transfers.CountLive(user.Id, TransferDirection.Upload) >= maxParallel)
                {
                    await activity.Log(
                        ActivityAction.Denied, user.Id, user.Username, false, folderId, folder!.Folder.Name,
                        target, 0, "Parallel upload limit reached", activityContext, ct
                    );
                    return new ObjectResult(
                        $"You already have {maxParallel} {(maxParallel == 1 ? "upload" : "uploads")} running. Wait for one to finish and try again.",
                        StatusCodes.Status429TooManyRequests
                    );
                }

                long remaining = long.MaxValue;
                if (folder!.UploadWindow != QuotaWindow.None && folder.UploadLimit is { } limit)
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

                // The account's ceiling across every share narrows the same allowance rather than
                // replacing it, so what is left is whichever of the two has less room.
                if (user!.UploadQuotaWindow != QuotaWindow.None && user.UploadQuotaBytes is { } accountLimit)
                {
                    var usage = await quotas.GetAccountUsage(user.Id, user.UploadQuotaWindow, ct);
                    var accountRemaining = accountLimit - usage.UploadedBytes;
                    if (accountRemaining <= 0)
                    {
                        await activity.Log(
                            ActivityAction.Denied, user.Id, user.Username, false, folderId, folder!.Folder.Name,
                            target, 0, "Account upload quota exhausted", activityContext, ct
                        );
                        return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
                    }
                    remaining = Math.Min(remaining, accountRemaining);
                }

                // Watched while it is copied rather than reported once it lands, so a slow or
                // abandoned upload is visible while it is happening. The handle outlives the copy
                // because the outcome is only settled by the checks below.
                using var transfer = transfers.Begin(
                    TransferDirection.Upload, user!.Id, user.Username, user.DisplayName,
                    folderId, folder!.Folder.Name, target, PartLength(part)
                );

                SavedFile saved;
                try
                {
                    // The scan runs inside Save, on the finished temp file and before it is moved
                    // into place, so an infected upload is never a file anyone could have listed or
                    // downloaded - not even for the moment it would take to delete it again.
                    // Stops reading the moment the file passes the per-file cap or the quota left, so
                    // an oversized file is refused at the limit rather than written out in full first.
                    var allowed = Math.Min(remaining, folder.MaxUploadFileSizeBytes ?? long.MaxValue);
                    await using var limited = new LimitedReadStream(part.Body, allowed);
                    await using var counted = new CountingStream(limited, transfer, leaveInnerOpen: true, ownsHandle: false);
                    saved = await files.Save(
                        folder, target, counted, conflict,
                        inspect: (temp, token) => this.Inspect(temp, name, token),
                        ct: ct
                    );
                }
                catch (UploadLimitExceededException ex)
                {
                    transfer.Fail("Exceeded the quota or the per-file limit");

                    await activity.Log(
                        ActivityAction.Denied, user!.Id, user.Username, false, folderId, folder!.Folder.Name,
                        target, ex.Limit, "Upload exceeded the quota or per-file limit", activityContext, ct
                    );
                    return new ObjectResult(
                        folder.MaxUploadFileSizeBytes is { } fileCap && remaining > fileCap
                            ? $"{name} is larger than this folder accepts."
                            : $"{name} is larger than the upload quota you have left.",
                        StatusCodes.Status413PayloadTooLarge
                    );
                }
                catch (FileRejectedException ex)
                {
                    transfer.Fail(ex.Message);

                    await activity.Log(
                        ActivityAction.Denied, user!.Id, user.Username, false, folderId, folder!.Folder.Name,
                        target, ex.Bytes, ex.Message, activityContext, ct
                    );

                    // 422 is the file itself being refused; anything else is the server admitting it
                    // could not tell, which is a condition of the server and may pass on a retry.
                    return new ObjectResult(ex.Message)
                    {
                        StatusCode = ex.Decision.Verdict == ScanVerdict.Infected
                            ? StatusCodes.Status422UnprocessableEntity
                            : StatusCodes.Status503ServiceUnavailable
                    };
                }

                var written = saved.Bytes;
                transfer.SetTotal(written);

                if (written > remaining || (folder.MaxUploadFileSizeBytes is { } cap && written > cap))
                {
                    // the real size only became knowable once the stream ran out; undo rather than
                    // let an over-quota file sit on disk
                    try { files.Delete(folder, saved.VirtualPath); } catch (FileAccessException) { }

                    transfer.Fail("Exceeded the quota or the per-file limit");

                    await activity.Log(
                        ActivityAction.Denied, user!.Id, user.Username, false, folderId, folder.Folder.Name,
                        target, written, "Upload exceeded the quota or per-file limit", activityContext, ct
                    );
                    return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
                }

                transfer.Complete();

                await quotas.RecordUpload(user!.Id, folderId, folder.UploadWindow, written, ct);
                await quotas.RecordAccountUpload(user.Id, user.UploadQuotaWindow, written, ct);
                await activity.Log(
                    ActivityAction.Upload, user.Id, user.Username, true, folderId, folder.Folder.Name,
                    saved.VirtualPath, written, UploadRules.RenamedDetail(target, saved), activityContext, ct
                );

                var info = files.GetFile(folder, saved.VirtualPath);
                uploaded.Add(new FileEntryInfo(info.Name, saved.VirtualPath, false, info.Length, info.LastWriteTimeUtc));
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

    /// <summary>
    /// The hook <see cref="IFileService.Save"/> calls on a finished upload while it is still a temp
    /// file. Throwing is how a file is refused - it is the only thing that stops the move.
    /// </summary>
    async Task Inspect(string tempPath, string fileName, CancellationToken ct)
    {
        var decision = await scanner.Scan(tempPath, fileName, ct);
        if (!decision.Allowed)
            throw new FileRejectedException(decision, new FileInfo(tempPath).Length);
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

    /// <summary>
    /// How big this part says it is, for the progress bar. The request's own Content-Length is no
    /// use: it covers every part plus the multipart envelope, so it would overstate a single file
    /// and be meaningless for several. Null when the client did not say, which is allowed - the
    /// upload then reports bytes and speed without a percentage.
    /// </summary>
    /// <summary>The validator a download is served with, and that If-Range and If-None-Match are compared to.</summary>
    internal static string ETagFor(FileInfo info)
        => $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";

    /// <summary>
    /// FromFile in all but the handle it opens: the same content type, download name, ETag and
    /// last-modified, so ranges and conditional GETs behave exactly as they did. The difference is
    /// the counting wrapper, which is what turns a download into something an administrator can
    /// watch. The transfer is registered inside the opener because a 304 or a 416 never opens the
    /// file, and neither should appear as a transfer that moved no bytes. A null <paramref name="begin"/>
    /// serves without watching, for a request that will send nothing. A null
    /// <paramref name="downloadName"/> serves the file inline.
    /// </summary>
    internal static FileDownloadResult Serve(FileInfo info, string etag, string? downloadName, HttpContext ctx, Func<ITransferHandle>? begin)
        => FileDownloadResult.FromOpener(
            _ =>
            {
                Stream file = new FileStream(info.FullName, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });

                if (begin is null)
                    return ValueTask.FromResult(file);

                // How much is actually being sent is only settled once the range has been
                // resolved, which happens after the stream is opened and before the first read.
                return ValueTask.FromResult<Stream>(new CountingStream(
                    file, begin(), () => ctx.Response.ContentLength ?? info.Length
                ));
            },
            info.Length,
            ContentTypes.ForFileName(info.FullName),
            downloadName,
            etag,
            info.LastWriteTimeUtc
        );

    static long? PartLength(MultipartSection part)
        => Int64.TryParse(part.Headers.GetFirst("Content-Length"), out var length) && length >= 0
            ? length
            : null;

    static string Normalize(string? path)
        => String.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Trim('/');

    static string Combine(string? parent, string name)
    {
        var prefix = Normalize(parent);
        return String.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
    }
}
