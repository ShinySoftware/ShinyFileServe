using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;
using CoreUser = ShinyFileServe.Core.Documents.User;

namespace ShinyFileServe.Server.PackageFeeds;

/// <summary>
/// Everything that decides who may use the package feeds. A request to <c>/nuget</c> or <c>/npm</c>
/// carries a personal access token or nothing; the token names an account, the account's groups
/// name what it may do, and a request with no token gets the anonymous set from the settings.
/// </summary>
/// <remarks>
/// <para>
/// Tokens are read from every place a package client puts one: <c>X-NuGet-ApiKey</c> for
/// <c>dotnet nuget push</c>, the password half of Basic for <c>nuget.config</c> credentials and
/// npm's <c>_auth</c>, and Bearer for npm's <c>_authToken</c>.
/// </para>
/// <para>
/// The portal's cookie, and the loopback sign-in, count for nothing here. A package client never has
/// either, and letting a browser's session or a local process inherit the machine owner's rights
/// would make "who pushed this" unanswerable - the log has to name a token's owner, every time.
/// </para>
/// </remarks>
public class PackageGateMiddleware(
    PackageFeedHost host,
    ISettingsManager settings,
    IAccessTokenManager tokens,
    IUserManager users,
    IActivityLogger activity,
    ICurrentUserResolver current
) : IHttpMiddleware
{
    const string UserItem = "fileserve.package-user";
    const string Realm = "ShinyFileServe packages";

    /// <summary>The account the gate resolved for this request, or null for an anonymous one.</summary>
    public static CoreUser? UserOf(HttpContext context)
        => context.Items.TryGetValue(UserItem, out var value) ? value as CoreUser : null;

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var feed = PackageFeedHost.Classify(context.Request.Path);
        if (feed == PackageFeed.None)
        {
            await next(context);
            return;
        }

        var config = settings.Current.PackageFeeds;
        host.Apply(config);

        // Mapped but switched off answers exactly like a route that was never there.
        if (feed == PackageFeed.NuGet ? !config.NuGetEnabled : !config.NpmEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var (required, write) = Classify(feed, context.Request.Method, context.Request.Path);
        var secret = ReadSecret(context.Request);
        var ct = context.RequestAborted;

        CoreUser? user = null;
        var granted = config.Anonymous & PackagePermissionsExtensions.Reads;

        if (secret is not null)
        {
            user = await tokens.Validate(secret, ct);
            if (user is null)
            {
                // Logged, unlike a missing token: a request without one is how every NuGet client
                // starts before the challenge, whereas a wrong one is somebody's config gone stale.
                await activity.Log(
                    ActivityAction.LoginFailed, null, "", success: false,
                    detail: $"Refused a token on the {Name(feed)} feed: unknown, expired or revoked",
                    context: current.GetActivityContext(context), ct: ct
                );
                await Refuse(context, feed, StatusCodes.Status401Unauthorized,
                    "The access token is unknown, expired or revoked, or its account can no longer sign in.");
                return;
            }

            var roles = await users.GetRolesFor(user, ct);
            granted = user.PackagePermissions(roles);

            // Who the bandwidth meter files this request under, and nothing else - no route here
            // reads the claims, and no cookie is issued for it.
            context.User = PrincipalFactory.Create(user, roles);
            context.Items[UserItem] = user;
        }
        else
        {
            // Whatever the cookie or the loopback sign-in said, a request without a token is anonymous.
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        if (required != PackagePermissions.None && !granted.Has(required))
        {
            if (user is null)
            {
                await Refuse(context, feed, StatusCodes.Status401Unauthorized,
                    $"This {Name(feed)} feed needs an access token. Create one from your account in the FileServe portal.");
                return;
            }

            await activity.Log(
                ActivityAction.Denied, user.Id, user.Username, success: false,
                path: context.Request.Path,
                detail: $"{Name(feed)} {required.ToDisplay().ToLowerInvariant()} refused: no permission",
                context: current.GetActivityContext(context), ct: ct
            );
            await Refuse(context, feed, StatusCodes.Status403Forbidden,
                $"Your account is not allowed to {required.ToDisplay().ToLowerInvariant()} on this {Name(feed)} feed.");
            return;
        }

        await next(context);

        if (write && user is not null && context.Response.StatusCode is >= 200 and < 300)
            await this.LogWrite(context, feed, user, ct);
    }

    async Task LogWrite(HttpContext context, PackageFeed feed, CoreUser user, CancellationToken ct)
    {
        var method = context.Request.Method;
        var published = context.Items.TryGetValue(PackageFeedHost.PublishedItem, out var item) ? item as string : null;

        var (action, detail) = (feed, method) switch
        {
            (PackageFeed.NuGet, "PUT") => (ActivityAction.PackagePublish, $"Pushed {published ?? "a NuGet package"}"),
            (PackageFeed.NuGet, "POST") => (ActivityAction.PackageRemove, "Relisted a NuGet package"),
            (PackageFeed.NuGet, _) => (ActivityAction.PackageRemove,
                host.NuGet.DeleteBehavior == Shiny.Net.HttpServer.NuGet.NuGetDeleteBehavior.Delete
                    ? "Deleted a NuGet package"
                    : "Unlisted a NuGet package"),
            (_, "DELETE") => (ActivityAction.PackageRemove, "Unpublished from npm"),
            _ => (ActivityAction.PackagePublish, published is not null ? $"Published {published} to npm" : "Updated an npm package")
        };

        await activity.Log(
            action, user.Id, user.Username, success: true,
            path: context.Request.Path, detail: detail,
            context: current.GetActivityContext(context), ct: ct
        );
    }

    /// <summary>
    /// What a request needs, and whether it changes anything. NuGet puts every write on its own
    /// verb; npm writes are all PUT and DELETE, apart from login and the audit endpoint, which
    /// only borrow the verb.
    /// </summary>
    internal static (PackagePermissions Required, bool Write) Classify(PackageFeed feed, string method, string path)
    {
        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

        if (feed == PackageFeed.NuGet)
        {
            if (read)
                return (PackagePermissions.NuGetRead, false);

            return HttpMethods.IsPut(method)
                ? (PackagePermissions.NuGetPush, true)
                : (PackagePermissions.NuGetDelete, true);
        }

        var rest = path.Length > PackageFeedHost.NpmPrefix.Length ? path[(PackageFeedHost.NpmPrefix.Length + 1)..] : "";

        // npm's own housekeeping. whoami needs a token but no permission - the library asks for
        // the user itself - and login and logout are how a token is had in the first place.
        if (rest is "" or "-/ping" || rest.StartsWith("-/whoami", StringComparison.Ordinal) || rest.StartsWith("-/user/", StringComparison.Ordinal))
            return (PackagePermissions.None, false);

        // npm audit POSTs the dependency tree; it reads, whatever the verb.
        if (read || rest.StartsWith("-/npm/v1/security/", StringComparison.Ordinal))
            return (PackagePermissions.NpmRead, false);

        return (PackagePermissions.NpmPublish, true);
    }

    /// <summary>
    /// The token the request carries, or null when it carries none. Empty credentials count as none:
    /// NuGet with nothing stored answers a challenge with <c>Basic Og==</c> - a blank name and
    /// password - and that is a client without a token, not a wrong one worth logging.
    /// </summary>
    static string? ReadSecret(HttpRequest request)
    {
        if (request.Headers.GetFirst("X-NuGet-ApiKey") is { } apiKey && !String.IsNullOrWhiteSpace(apiKey))
            return apiKey.Trim();

        if (request.Headers.GetFirst(HeaderNames.Authorization) is not { Length: > 0 } header)
            return null;

        string? secret = null;

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            secret = header["Bearer ".Length..];
        }
        else if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var colon = decoded.IndexOf(':');

                // The user name is whatever the config file says; the token alone decides who this is.
                secret = colon >= 0 ? decoded[(colon + 1)..] : decoded;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return String.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
    }

    static string Name(PackageFeed feed) => feed == PackageFeed.NuGet ? "NuGet" : "npm";

    /// <summary>
    /// A refusal in the shape each client prints. NuGet only offers its stored credentials after a
    /// Basic challenge, so a 401 carries one; npm reads an <c>error</c> field out of JSON.
    /// </summary>
    static async Task Refuse(HttpContext context, PackageFeed feed, int status, string message)
    {
        context.Response.StatusCode = status;

        if (feed == PackageFeed.NuGet)
        {
            if (status == StatusCodes.Status401Unauthorized)
                context.Response.Headers.Set("WWW-Authenticate", $"Basic realm=\"{Realm}\"");

            await context.Response.WriteTextAsync(message, "text/plain; charset=utf-8", context.RequestAborted);
            return;
        }

        var json = JsonEncodedText.Encode(message).ToString();
        await context.Response.WriteTextAsync($"{{\"error\":\"{json}\"}}", "application/json", context.RequestAborted);
    }
}
