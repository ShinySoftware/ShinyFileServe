using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Files;
using Shiny.Net.HttpServer.Security;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Server.Transfers;
using ShinyFileServe.Shared;
using CoreUser = ShinyFileServe.Core.Documents.User;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// What a share link opens. Public, because an "anyone" link is opened by people with no account;
/// a link for named accounts asks for a session itself. Every request re-checks the whole chain -
/// the link, its expiry, the creator, and the creator's rights in the share today - so revoking a
/// grant or an account closes its links on the next click rather than on some later sweep.
/// </summary>
/// <remarks>
/// Downloads are charged to the creator's quotas, folder and account both. The link spends their
/// allowance: an "anyone" link that metered nobody would be a way round every quota on the server.
/// </remarks>
[Route("/api/shared")]
public class SharedEndpoints(
    ICurrentUserResolver current,
    IShareLinkManager links,
    IUserManager users,
    IAccessResolver access,
    IFolderManager folders,
    IFileService files,
    IQuotaTracker quotas,
    ITransferMonitor transfers,
    IActivityLogger activity
)
{
    [Get("/{token}")]
    [AllowAnonymous]
    [Produces(200, typeof(SharedItem))]
    [Produces(401)]
    [Produces(403)]
    [Produces(404)]
    [Produces(410)]
    public async Task<IActionResult> Describe(string token, HttpContext ctx, CancellationToken ct = default)
    {
        var opened = await this.Open(ctx, token, ct);
        if (opened.Refused is not null)
            return opened.Refused;

        try
        {
            var link = opened.Link!;
            var full = files.ResolvePath(opened.Access!, link.Path);
            var (size, modified) = link.IsDirectory
                ? (0L, Directory.GetLastWriteTimeUtc(full))
                : (new FileInfo(full).Length, File.GetLastWriteTimeUtc(full));

            var name = link.Path.Length == 0 ? opened.Access!.Folder.Name : link.Path[(link.Path.LastIndexOf('/') + 1)..];

            // The link itself is counted here and only here - this is the one request a page makes
            // once per visit, where the files behind it are fetched many times over.
            await links.RecordOpen(link, ct);

            return new OkObjectResult(new SharedItem(
                link.Title,
                name,
                link.IsDirectory,
                opened.Creator!.DisplayName,
                link.ExpiresAtUtc,
                size,
                new DateTimeOffset(modified, TimeSpan.Zero)
            ));
        }
        catch (FileAccessException)
        {
            return Gone();
        }
    }

    /// <summary>One directory inside a shared folder. <c>path</c> is relative to the link.</summary>
    [Get("/{token}/list")]
    [AllowAnonymous]
    [Produces(200, typeof(SharedListing))]
    [Produces(401)]
    [Produces(404)]
    public async Task<IActionResult> List(string token, HttpContext ctx, string? path = null, CancellationToken ct = default)
    {
        var opened = await this.Open(ctx, token, ct);
        if (opened.Refused is not null)
            return opened.Refused;

        var link = opened.Link!;
        if (!link.IsDirectory)
            return new NotFoundResult();

        var relative = Normalize(path);
        var inShare = Combine(link.Path, relative);

        try
        {
            var annotations = await folders.GetAnnotations(link.FolderId, ct);

            // Paths go back relative to the link: the share's own layout above the shared folder is
            // none of the visitor's business, and it would be the first thing a URL leaked.
            var entries = files.List(opened.Access!, inShare)
                .Select(e =>
                {
                    var entry = annotations.TryGetValue(e.Path, out var note)
                        ? e with { DisplayName = note.DisplayName, Description = note.Description }
                        : e;
                    return entry with { Path = Relative(link.Path, e.Path) };
                })
                .ToList();

            await activity.Log(
                ActivityAction.List, opened.Viewer?.Id, Who(opened), true, link.FolderId, opened.Access!.Folder.Name, inShare,
                detail: Via(link), context: current.GetActivityContext(ctx), ct: ct
            );

            return new OkObjectResult(new SharedListing(relative, entries));
        }
        catch (FileAccessException ex)
        {
            return new NotFoundObjectResult(ex.Message);
        }
    }

    /// <summary>
    /// A file behind the link, inline so a photo can be an <c>img</c>, or as an attachment with
    /// <c>download=true</c>. A link to one file serves that file whatever <c>path</c> says.
    /// </summary>
    [Get("/{token}/file")]
    [AllowAnonymous]
    [Produces(200)]
    [Produces(401)]
    [Produces(404)]
    [Produces(429)]
    public async Task<IActionResult> Download(string token, HttpContext ctx, string? path = null, bool download = false, CancellationToken ct = default)
    {
        var opened = await this.Open(ctx, token, ct);
        if (opened.Refused is not null)
            return opened.Refused;

        var link = opened.Link!;
        var folder = opened.Access!;
        var creator = opened.Creator!;
        var inShare = link.IsDirectory ? Combine(link.Path, Normalize(path)) : link.Path;
        var context = current.GetActivityContext(ctx);

        FileInfo info;
        try
        {
            info = files.GetFile(folder, inShare);
        }
        catch (FileAccessException ex)
        {
            return new NotFoundObjectResult(ex.Message);
        }

        // The same two ceilings a download through the browser meets, but the creator's: theirs is
        // the allowance the link is spending.
        if (folder.DownloadWindow != QuotaWindow.None && folder.DownloadLimit is { } limit)
        {
            var usage = await quotas.GetUsage(creator.Id, link.FolderId, folder.DownloadWindow, ct);
            if (usage.DownloadedBytes + info.Length > limit)
                return await this.OverQuota(opened, inShare, info.Length, "the share's download quota", context, ct);
        }

        if (creator.DownloadQuotaWindow != QuotaWindow.None && creator.DownloadQuotaBytes is { } accountLimit)
        {
            var usage = await quotas.GetAccountUsage(creator.Id, creator.DownloadQuotaWindow, ct);
            if (usage.DownloadedBytes + info.Length > accountLimit)
                return await this.OverQuota(opened, inShare, info.Length, "the account download quota", context, ct);
        }

        await quotas.RecordDownload(creator.Id, link.FolderId, folder.DownloadWindow, info.Length, ct);
        await quotas.RecordAccountDownload(creator.Id, creator.DownloadQuotaWindow, info.Length, ct);
        await activity.Log(
            ActivityAction.Download, opened.Viewer?.Id, Who(opened), true, link.FolderId, folder.Folder.Name, inShare, info.Length,
            Via(link), context, ct
        );

        var transferUser = opened.Viewer ?? creator;
        var transferName = opened.Viewer is null ? $"{creator.DisplayName} (link)" : opened.Viewer.DisplayName;

        return FileDownloadResult.FromOpener(
            _ =>
            {
                Stream file = new FileStream(info.FullName, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });

                var handle = transfers.Begin(
                    TransferDirection.Download, transferUser.Id, transferUser.Username, transferName,
                    link.FolderId, folder.Folder.Name, inShare, info.Length
                );

                return ValueTask.FromResult<Stream>(new CountingStream(
                    file, handle, () => ctx.Response.ContentLength ?? info.Length
                ));
            },
            info.Length,
            ContentTypes.ForFileName(info.FullName),
            // No name serves it inline, which is what lets a photo be an img; a name makes it a download.
            download ? info.Name : null,
            $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"",
            info.LastWriteTimeUtc
        );
    }

    // -----------------------------------------------------------------------
    // The chain every request walks
    // -----------------------------------------------------------------------

    record Opened(ShareLink? Link, CoreUser? Creator, CoreUser? Viewer, FolderAccess? Access, IActionResult? Refused);

    async Task<Opened> Open(HttpContext ctx, string token, CancellationToken ct)
    {
        var link = await links.FindByToken(token, ct);
        if (link is null)
            return Refuse(new NotFoundObjectResult("There is no such link. It may have been revoked."));

        var now = DateTimeOffset.UtcNow;
        if (link.IsExpired(now))
            return Refuse(new ObjectResult("This link has expired.", 410));

        // The link lends its creator's rights, so a creator who can no longer download from the
        // share - or no longer exists - leaves nothing to lend.
        var creator = await users.GetUser(link.CreatedByUserId, ct);
        if (creator is null || creator.IsExpired(now))
            return Refuse(Gone());

        var granted = await access.GetAccess(creator, link.FolderId, ct);
        if (granted is null || !granted.Permissions.Has(FolderPermissions.DownloadFile))
            return Refuse(Gone());

        // Narrowed to reading, whatever the creator could do: a link never uploads or deletes.
        var narrowed = granted with
        {
            Permissions = FolderPermissions.List | FolderPermissions.DownloadFile
        };

        var viewer = await current.Resolve(ctx, ct);

        if (link.Audience == ShareLinkAudience.Accounts)
        {
            if (viewer is null || viewer.IsAnonymous)
            {
                // 401 with a body is what tells the page to offer the sign-in form rather than
                // reporting a dead link.
                return Refuse(new ObjectResult("Sign in to open this link.", StatusCodes.Status401Unauthorized));
            }

            var allowed = link.UserIds.Contains(viewer.Id)
                || viewer.Id == creator.Id
                || viewer.IsAdministrator(await users.GetRolesFor(viewer, ct));

            if (!allowed)
            {
                await activity.Log(
                    ActivityAction.Denied, viewer.Id, viewer.Username, false, link.FolderId, granted.Folder.Name, link.Path,
                    detail: $"Not a recipient of {Via(link)}", context: current.GetActivityContext(ctx), ct: ct
                );
                return Refuse(new ObjectResult(
                    $"This link was shared with other accounts. You are signed in as {viewer.DisplayName}.",
                    StatusCodes.Status403Forbidden
                ));
            }
        }

        return new Opened(link, creator, viewer, narrowed, null);
    }

    static Opened Refuse(IActionResult result) => new(null, null, null, null, result);

    static IActionResult Gone()
        => new ObjectResult("What this link pointed at is no longer available.", 410);

    async Task<IActionResult> OverQuota(Opened opened, string path, long bytes, string which, ActivityContext context, CancellationToken ct)
    {
        await activity.Log(
            ActivityAction.Denied, opened.Viewer?.Id, Who(opened), false, opened.Link!.FolderId, opened.Access!.Folder.Name, path, bytes,
            $"{Via(opened.Link)} - {opened.Creator!.Username} is over {which}", context, ct
        );
        return new ObjectResult(
            "This link has used up the download allowance of the person who shared it. Try again later.",
            StatusCodes.Status429TooManyRequests
        );
    }

    /// <summary>The name on the activity line: the visitor when they signed in, otherwise which link.</summary>
    static string Who(Opened opened)
        => opened.Viewer?.Username ?? $"link from {opened.Creator!.Username}";

    static string Via(ShareLink link) => $"share link \"{link.Title}\" from {link.CreatedByUsername}";

    static string Relative(string root, string path)
        => root.Length == 0 ? path : path.Length > root.Length ? path[(root.Length + 1)..] : "";

    static string Normalize(string? path)
        => String.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Trim('/');

    static string Combine(string root, string relative)
        => root.Length == 0 ? relative : relative.Length == 0 ? root : $"{root}/{relative}";
}
