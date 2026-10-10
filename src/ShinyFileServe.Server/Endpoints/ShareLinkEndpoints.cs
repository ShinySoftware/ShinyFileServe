using Shiny.AppDeviceBridge.Tunnel;
using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;
using CoreUser = ShinyFileServe.Core.Documents.User;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// Making, listing and revoking share links. Anybody who may download something may share it - a
/// link lends its creator's rights rather than granting new ones, so it is no wider than what they
/// could already have carried out of the door themselves.
/// </summary>
[Route("/api/links")]
[Authorize]
public class ShareLinkEndpoints(
    ICurrentUserResolver current,
    IShareLinkManager links,
    IUserManager users,
    IAccessResolver access,
    IFolderManager folders,
    IFileService files,
    ISettingsManager settings,
    AppDeviceBridgeTunnel tunnel,
    IActivityLogger activity
)
{
    /// <summary>
    /// The caller's own links. An administrator may ask for everybody's with <c>all=true</c>, which
    /// is how a link somebody else made gets revoked.
    /// </summary>
    [Get]
    [Produces(200, typeof(List<ShareLinkInfo>))]
    public async Task<IActionResult> List(HttpContext ctx, bool all = false, CancellationToken ct = default)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        var isAdmin = user.IsAdministrator(await users.GetRolesFor(user, ct));
        var found = all && isAdmin
            ? await links.GetAll(ct)
            : await links.GetCreatedBy(user.Id, ct);

        return new OkObjectResult(await this.ToInfo(ctx, found, ct));
    }

    /// <summary>Links other people made for this account specifically.</summary>
    [Get("/shared-with-me")]
    [Produces(200, typeof(List<ShareLinkInfo>))]
    public async Task<IActionResult> SharedWithMe(HttpContext ctx, CancellationToken ct = default)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        var now = DateTimeOffset.UtcNow;
        var found = (await links.GetSharedWith(user.Id, ct)).Where(x => !x.IsExpired(now)).ToList();
        return new OkObjectResult(await this.ToInfo(ctx, found, ct));
    }

    /// <summary>
    /// Who a link can be addressed to: every account that signs in with a password and has not
    /// expired. Anonymous accounts are left out - anybody can be one, so naming one is the same as
    /// "anyone with the link", and that is a choice the form already offers honestly.
    /// </summary>
    [Get("/recipients")]
    [Produces(200, typeof(List<ShareRecipient>))]
    public async Task<IActionResult> Recipients(HttpContext ctx, CancellationToken ct = default)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        var now = DateTimeOffset.UtcNow;
        var people = (await users.GetUsers(ct))
            .Where(u => u.Id != user.Id && !u.IsAnonymous && !u.IsExpired(now))
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new ShareRecipient(u.Id, u.Username, u.DisplayName))
            .ToList();

        return new OkObjectResult(people);
    }

    [Post]
    [Produces(200, typeof(ShareLinkInfo))]
    [Produces(400)]
    [Produces(403)]
    [Produces(404)]
    public async Task<IActionResult> Create(CreateShareLinkRequest request, HttpContext ctx, CancellationToken ct = default)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        // Anybody at all can be an anonymous account, so a link made by one would be nobody's.
        if (user.IsAnonymous)
            return new ObjectResult("Sign in with your own account to share files.", StatusCodes.Status403Forbidden);

        var folder = await access.GetAccess(user, request.FolderId, ct);
        if (folder is null || !folder.Permissions.Has(FolderPermissions.List))
            return new NotFoundResult();

        if (!folder.Permissions.Has(FolderPermissions.DownloadFile))
            return new ObjectResult("You can only share what you are allowed to download.", StatusCodes.Status403Forbidden);

        string full;
        try
        {
            full = files.ResolvePath(folder, request.Path);
        }
        catch (FileAccessException ex)
        {
            return new BadRequestObjectResult(ex.Message);
        }

        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
            return new NotFoundObjectResult("That file or folder is not there any more.");

        if (request.ExpiresAtUtc is { } expires && expires <= DateTimeOffset.UtcNow)
            return new BadRequestObjectResult("The expiry has to be in the future.");

        var recipients = new List<string>();
        if (request.Audience == ShareLinkAudience.Accounts)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var id in request.UserIds.Distinct())
            {
                var person = await users.GetUser(id, ct);
                if (person is null || person.IsAnonymous || person.IsExpired(now))
                    return new BadRequestObjectResult("One of those accounts cannot be shared with.");

                recipients.Add(person.Id);
            }

            if (recipients.Count == 0)
                return new BadRequestObjectResult("Pick at least one account, or share with anyone who has the link.");
        }

        var path = Normalize(request.Path);
        var name = path.Length == 0 ? folder.Folder.Name : path[(path.LastIndexOf('/') + 1)..];
        var title = String.IsNullOrWhiteSpace(request.Title) ? name : request.Title.Trim();

        var link = await links.Create(user, folder.Folder.Id, path, isDirectory, title, request.Audience, recipients, request.ExpiresAtUtc, ct);

        await activity.Log(
            ActivityAction.ShareLinkCreated, user.Id, user.Username, true, folder.Folder.Id, folder.Folder.Name, path,
            detail: Describe(link, recipients.Count), context: current.GetActivityContext(ctx), ct: ct
        );

        return new OkObjectResult((await this.ToInfo(ctx, [link], ct))[0]);
    }

    /// <summary>Takes a link back. Whoever made it may, and so may an administrator.</summary>
    [Delete("/{id}")]
    [Produces(204)]
    [Produces(404)]
    public async Task<IActionResult> Revoke(string id, HttpContext ctx, CancellationToken ct = default)
    {
        var user = await current.Resolve(ctx, ct);
        if (user is null)
            return new UnauthorizedResult();

        var link = await links.Get(id, ct);
        if (link is null)
            return new NotFoundResult();

        if (link.CreatedByUserId != user.Id && !user.IsAdministrator(await users.GetRolesFor(user, ct)))
            return new NotFoundResult();

        await links.Delete(link.Id, ct);

        var folder = await folders.GetFolder(link.FolderId, ct);
        await activity.Log(
            ActivityAction.ShareLinkRevoked, user.Id, user.Username, true, link.FolderId, folder?.Name, link.Path,
            detail: $"\"{link.Title}\", made by {link.CreatedByUsername}", context: current.GetActivityContext(ctx), ct: ct
        );

        return new NoContentResult();
    }

    async Task<List<ShareLinkInfo>> ToInfo(HttpContext ctx, IReadOnlyList<ShareLink> found, CancellationToken ct)
    {
        var baseUrl = ShareLinkAddress.BaseUrl(ctx, tunnel, settings.Current.Port);
        var now = DateTimeOffset.UtcNow;

        // A few dozen links name a handful of folders and people; look each up once.
        var folderNames = new Dictionary<string, string>();
        var people = new Dictionary<string, CoreUser?>();

        var results = new List<ShareLinkInfo>(found.Count);
        foreach (var link in found)
        {
            if (!folderNames.TryGetValue(link.FolderId, out var folderName))
            {
                folderName = (await folders.GetFolder(link.FolderId, ct))?.Name ?? "(removed)";
                folderNames[link.FolderId] = folderName;
            }

            var recipients = new List<ShareRecipient>();
            foreach (var id in link.UserIds)
            {
                if (!people.TryGetValue(id, out var person))
                {
                    person = await users.GetUser(id, ct);
                    people[id] = person;
                }

                if (person is not null)
                    recipients.Add(new ShareRecipient(person.Id, person.Username, person.DisplayName));
            }

            results.Add(new ShareLinkInfo(
                link.Id,
                $"{baseUrl}/s/{link.Token}",
                link.FolderId,
                folderName,
                link.Path,
                link.IsDirectory,
                link.Title,
                link.Audience,
                recipients,
                link.CreatedByUserId,
                link.CreatedByUsername,
                link.CreatedUtc,
                link.ExpiresAtUtc,
                link.OpenCount,
                link.LastOpenedUtc,
                link.IsExpired(now)
            ));
        }

        return results;
    }

    static string Describe(ShareLink link, int recipients)
    {
        var who = link.Audience == ShareLinkAudience.Anyone
            ? "anyone with the link"
            : recipients == 1 ? "1 account" : $"{recipients} accounts";

        var until = link.ExpiresAtUtc is { } expires ? $", until {expires:yyyy-MM-dd HH:mm} UTC" : "";
        return $"\"{link.Title}\" for {who}{until}";
    }

    static string Normalize(string? path)
        => String.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Trim('/');
}

/// <summary>Which address a link should be written with - the one somebody elsewhere can actually reach.</summary>
static class ShareLinkAddress
{
    /// <summary>
    /// The public tunnel when it is open; otherwise the address the browser used, unless that was
    /// this machine talking to itself; otherwise the first LAN address. A link copied out of the
    /// desktop window would be <c>127.0.0.1</c> without the last step, which works for nobody else.
    /// </summary>
    public static string BaseUrl(HttpContext ctx, AppDeviceBridgeTunnel tunnel, int port)
    {
        if (tunnel.PublicUrl is { } publicUrl)
            return publicUrl.GetLeftPart(UriPartial.Authority);

        // Host arrives as the header was written, port and all.
        var authority = ctx.Request.Host;
        if (!String.IsNullOrEmpty(authority)
            && Uri.TryCreate($"http://{authority}", UriKind.Absolute, out var asked)
            && !IsLoopbackName(asked.Host))
        {
            return $"{ctx.Request.Scheme}://{authority}";
        }

        // The port this request came in on rather than the stored setting, which can be ahead of a
        // listener that has not been rebound yet.
        var listening = ctx.Connection.LocalPort > 0 ? ctx.Connection.LocalPort : port;
        return NetworkAddresses.LocalUrls(listening).FirstOrDefault() ?? $"{ctx.Request.Scheme}://{authority}";
    }

    static bool IsLoopbackName(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address));
}
