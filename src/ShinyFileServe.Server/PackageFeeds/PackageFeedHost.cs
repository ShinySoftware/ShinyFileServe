using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.NuGet;
using Shiny.Net.HttpServer.Npm;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.PackageFeeds;

public enum PackageFeed
{
    None,
    NuGet,
    Npm
}

/// <summary>
/// Owns the options objects the NuGet feed and npm registry were mapped with. Both libraries read
/// their options on every request, so the settings an administrator saves are applied by writing
/// them here - no remap, and no restart.
/// </summary>
/// <remarks>
/// Neither library is asked to authenticate anybody. <see cref="PackageGateMiddleware"/> runs in front
/// of both, turns a token into an account and an account into package permissions, and only lets
/// through what those allow; the libraries are configured to take its word for it.
/// </remarks>
public class PackageFeedHost
{
    public const string NuGetPrefix = "/nuget";
    public const string NpmPrefix = "/npm";

    /// <summary>Where the package (and version) a write touched is left for the activity log.</summary>
    internal const string PublishedItem = "fileserve.package-published";

    static readonly Uri NpmUpstream = new("https://registry.npmjs.org");

    public PackageFeedHost(string packagesDirectory, IServiceProvider services)
    {
        var tokens = services.GetRequiredService<IAccessTokenManager>();
        var users = services.GetRequiredService<IUserManager>();
        var passwords = services.GetRequiredService<IPasswordPolicy>();
        var activity = services.GetRequiredService<IActivityLogger>();

        this.NuGet = new NuGetFeedOptions
        {
            Store = new DiskNuGetPackageStore(Path.Combine(packagesDirectory, "nuget")),

            // The gate has already checked the token and the push permission by the time a write
            // gets here. Key checking in the library would only demand the same token twice.
            RequireApiKey = false,
            OnPackagePushedAsync = ctx =>
            {
                ctx.HttpContext.Items[PublishedItem] = $"{ctx.Package.Id} {ctx.Package.Version}";
                return ValueTask.CompletedTask;
            }
        };

        this.Npm = new NpmRegistryOptions
        {
            Store = new DiskNpmPackageStore(Path.Combine(packagesDirectory, "npm")),

            // Reads are the gate's to allow, anonymously or not. What npm itself still needs a user
            // name for - publish, the maintainer list, whoami - is the account the gate resolved.
            RequireTokenForReads = false,
            ValidateTokenAsync = ctx => ValueTask.FromResult(PackageGateMiddleware.UserOf(ctx.HttpContext)?.Username),
            LoginAsync = ctx => Login(ctx, users, tokens, passwords, activity),
            OnPublishedAsync = ctx =>
            {
                ctx.HttpContext.Items[PublishedItem] = $"{ctx.PackageName}@{ctx.Version.Version}";
                return ValueTask.CompletedTask;
            }
        };
    }

    public NuGetFeedOptions NuGet { get; }
    public NpmRegistryOptions Npm { get; }

    /// <summary>Which feed, if any, a path belongs to.</summary>
    public static PackageFeed Classify(string path)
    {
        if (IsUnder(path, NuGetPrefix))
            return PackageFeed.NuGet;

        if (IsUnder(path, NpmPrefix))
            return PackageFeed.Npm;

        return PackageFeed.None;
    }

    static bool IsUnder(string path, string prefix)
        => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (path.Length == prefix.Length || path[prefix.Length] == '/');

    /// <summary>Brings the library options in line with the stored settings. Cheap; called per request.</summary>
    public void Apply(PackageFeedSettings settings)
    {
        this.NuGet.DeleteBehavior = settings.NuGetPermanentDelete ? NuGetDeleteBehavior.Delete : NuGetDeleteBehavior.Unlist;
        this.Npm.AllowUnpublish = settings.NpmAllowUnpublish;
        this.Npm.Upstream = settings.NpmUseUpstream ? NpmUpstream : null;
    }

    /// <summary>
    /// <c>npm login --auth-type=legacy</c>: the account's own password in, a fresh token out. It goes
    /// through the same sign-in as the portal, so a wrong password counts towards the lockout, and
    /// it refuses an account that has no npm permission at all rather than mint it a useless token.
    /// </summary>
    static async ValueTask<string?> Login(
        NpmLoginContext ctx,
        IUserManager users,
        IAccessTokenManager tokens,
        IPasswordPolicy passwords,
        IActivityLogger activity
    )
    {
        var ct = ctx.HttpContext.RequestAborted;
        var context = new ActivityContext(
            ctx.HttpContext.Connection.RemoteIpAddress?.ToString(),
            ctx.HttpContext.Request.Headers.GetFirst("User-Agent")
        );

        var result = await users.Authenticate(ctx.UserName, ctx.Password, ct);
        if (result.User is not { } user)
        {
            await activity.Log(
                ActivityAction.LoginFailed, result.JustLocked?.Id, ctx.UserName, success: false,
                detail: "npm login refused", context: context, ct: ct
            );
            return null;
        }

        var roles = await users.GetRolesFor(user, ct);
        if (!user.PackagePermissions(roles).Any(PackagePermissionsExtensions.Npm) || passwords.MustChangePassword(user))
        {
            await activity.Log(
                ActivityAction.Denied, user.Id, user.Username, success: false,
                detail: passwords.MustChangePassword(user)
                    ? "npm login refused: the password has expired"
                    : "npm login refused: no npm permission",
                context: context, ct: ct
            );
            return null;
        }

        var (_, secret) = await tokens.Create(user, $"npm login {DateTimeOffset.UtcNow:yyyy-MM-dd}", null, ct);
        await activity.Log(
            ActivityAction.Login, user.Id, user.Username, success: true,
            detail: "Signed in with npm login; a token was issued", context: context, ct: ct
        );
        return secret;
    }
}

public static class PackageFeedRegistration
{
    /// <summary>
    /// Maps both feeds. They are always mapped and switched on and off by the gate, so turning one
    /// on in the settings takes effect on the next request.
    /// </summary>
    public static void MapPackageFeeds(this HttpServer server)
    {
        var services = server.Services ?? throw new InvalidOperationException("The package feeds need the server's container.");
        var host = services.GetRequiredService<PackageFeedHost>();

        // Authorization here is the gate's, not the route table's: package clients carry tokens,
        // not the portal's cookie, and an [Authorize] fallback would turn them all away.
        server.MapNuGetFeed(PackageFeedHost.NuGetPrefix, host.NuGet).AllowAnonymous();
        server.MapNpmRegistry(PackageFeedHost.NpmPrefix, host.Npm).AllowAnonymous();
    }
}
