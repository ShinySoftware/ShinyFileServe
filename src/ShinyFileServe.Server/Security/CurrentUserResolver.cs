using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Shared;
using ShinyFileServe.Core.Documents;
using CoreUser = ShinyFileServe.Core.Documents.User;

namespace ShinyFileServe.Server.Security;

public static class ClaimTypesEx
{
    public const string AdminRole = "admin";
}

/// <summary>
/// Turns the cookie's <see cref="ClaimsPrincipal"/> back into the stored user. Every endpoint that
/// touches data goes through this rather than trusting the claims alone, so an account that has
/// expired stops working on its next request instead of when the cookie happens to run out.
/// </summary>
public interface ICurrentUserResolver
{
    Task<CoreUser?> Resolve(HttpContext context, CancellationToken ct = default);
    ActivityContext GetActivityContext(HttpContext context);
}

public class CurrentUserResolver(IUserManager users, TimeProvider time) : ICurrentUserResolver
{
    public async Task<CoreUser?> Resolve(HttpContext context, CancellationToken ct = default)
    {
        var id = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (String.IsNullOrEmpty(id))
            return null;

        var user = await users.GetUser(id, ct);
        return user is not null && !user.IsExpired(time.GetUtcNow()) ? user : null;
    }

    public ActivityContext GetActivityContext(HttpContext context) => new(
        context.Connection.RemoteIpAddress?.ToString(),
        context.Request.Headers.GetFirst("User-Agent")
    );
}

public static class PrincipalFactory
{
    public const string AuthenticationScheme = "Cookies";

    /// <summary>Present when the session was granted for being on the server's own machine.</summary>
    public const string LocalSessionClaim = "local_session";

    /// <summary>Ties a cookie to the password it was signed in with. See <see cref="PasswordStampOf"/>.</summary>
    public const string PasswordStampClaim = "pwd_stamp";

    public static ClaimsPrincipal Create(CoreUser user, IReadOnlyList<Role> roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new("display_name", user.DisplayName),
            new(PasswordStampClaim, PasswordStampOf(user))
        };

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role.Name));

        // Admin comes either from the account or from any group it belongs to, and lands here as
        // one claim so [Authorize(Roles = "admin")] does not have to know which.
        if (user.IsAdministrator(roles))
            claims.Add(new Claim(ClaimTypes.Role, ClaimTypesEx.AdminRole));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationScheme));
    }

    /// <summary>
    /// A short fingerprint of the stored password. Setting a password writes a new salt and hash, so
    /// the fingerprint changes with it and every cookie signed in under the old one stops matching -
    /// which is what "change my password" is expected to do to a session left open somewhere else.
    /// Derived rather than stored so there is nothing extra to keep in step.
    /// </summary>
    public static string PasswordStampOf(CoreUser user)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(user.PasswordSalt + ":" + user.PasswordHash));
        return Convert.ToHexString(hash, 0, 8);
    }

    public static CurrentUser ToDto(
        CoreUser user,
        IReadOnlyList<Role> roles,
        bool isLocalSession = false,
        bool mustChangePassword = false
    )
        => new(
            user.Id,
            user.Username,
            user.DisplayName,
            user.IsAdministrator(roles),
            roles.Select(r => r.Name).ToList(),
            isLocalSession,
            mustChangePassword,
            user.IsAnonymous,
            user.MaxParallelUploads
        );
}
