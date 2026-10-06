using System.Security.Cryptography;
using System.Text;
using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;

namespace ShinyFileServe.Core.Services;

/// <summary>A secret that checked out: the token it belongs to and the account behind it.</summary>
public record AccessTokenMatch(AccessToken Token, User User);

public interface IAccessTokenManager
{
    /// <summary>Issues a token. The secret in the result exists nowhere else - it is not stored.</summary>
    Task<(AccessToken Token, string Secret)> Create(User user, string name, DateTimeOffset? expiresAtUtc, CancellationToken ct = default);

    Task<IReadOnlyList<AccessToken>> GetTokens(string userId, CancellationToken ct = default);

    /// <summary>Every token on the server, newest first. For administrators only.</summary>
    Task<IReadOnlyList<AccessToken>> GetAllTokens(CancellationToken ct = default);

    /// <summary>False when the user holds no token by that id - somebody else's included.</summary>
    Task<bool> Revoke(string userId, string tokenId, CancellationToken ct = default);

    /// <summary>Removes a token whoever holds it, and hands back what was removed. For administrators only.</summary>
    Task<AccessToken?> RevokeAny(string tokenId, CancellationToken ct = default);

    /// <summary>
    /// The token and account a secret belongs to, or null for anything that should not be let in: an
    /// unknown or expired token, or one whose account has expired, locked, been deleted or become anonymous.
    /// </summary>
    Task<AccessTokenMatch?> Validate(string secret, CancellationToken ct = default);
}

public class AccessTokenManager(IDocumentStore store, IUserManager users, TimeProvider time) : IAccessTokenManager
{
    /// <summary>
    /// Marks the string as ours, so a token pasted somewhere it should not be is recognisable to a
    /// secret scanner and to a person, and so a password typed in its place can be told apart cheaply.
    /// </summary>
    public const string Prefix = "sfs_";

    /// <summary>
    /// How stale <see cref="AccessToken.LastUsedUtc"/> may get. A restore sends dozens of requests a
    /// second; a write for each would be a database write per request for a date shown to the day.
    /// </summary>
    static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(15);

    public async Task<(AccessToken Token, string Secret)> Create(User user, string name, DateTimeOffset? expiresAtUtc, CancellationToken ct = default)
    {
        // Anybody may become an anonymous account, so a token minted from one would be a credential
        // with nobody behind it - and one that outlived the account being made anonymous.
        if (user.IsAnonymous)
            throw new InvalidOperationException("An account that signs in without a password cannot hold access tokens.");

        if (String.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A token needs a name.", nameof(name));

        var secret = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new AccessToken
        {
            Id = Ids.New(),
            UserId = user.Id,
            Name = name.Trim(),
            TokenHash = Hash(secret),
            Hint = secret[^4..],
            CreatedUtc = time.GetUtcNow(),
            ExpiresAtUtc = expiresAtUtc
        };
        await store.Insert(token, cancellationToken: ct);
        return (token, secret);
    }

    public async Task<IReadOnlyList<AccessToken>> GetTokens(string userId, CancellationToken ct = default)
        => (await store.Query<AccessToken>().Where(x => x.UserId == userId).ToList(ct))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();

    public async Task<IReadOnlyList<AccessToken>> GetAllTokens(CancellationToken ct = default)
        => (await store.Query<AccessToken>().ToList(ct))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();

    public async Task<AccessToken?> RevokeAny(string tokenId, CancellationToken ct = default)
    {
        var token = await store.Get<AccessToken>(tokenId, cancellationToken: ct);
        if (token is not null)
            await store.Remove<AccessToken>(tokenId, cancellationToken: ct);

        return token;
    }

    public async Task<bool> Revoke(string userId, string tokenId, CancellationToken ct = default)
    {
        // Looked up by owner as well as id, so one account cannot revoke another's by guessing.
        var token = await store.Get<AccessToken>(tokenId, cancellationToken: ct);
        if (token is null || token.UserId != userId)
            return false;

        await store.Remove<AccessToken>(tokenId, cancellationToken: ct);
        return true;
    }

    public async Task<AccessTokenMatch?> Validate(string secret, CancellationToken ct = default)
    {
        if (String.IsNullOrEmpty(secret) || !secret.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        // The lookup is by hash, so its timing says nothing about how much of a guess was right.
        var hash = Hash(secret);
        var token = (await store.Query<AccessToken>().Where(x => x.TokenHash == hash).ToList(ct)).FirstOrDefault();
        var now = time.GetUtcNow();

        if (token is null || token.IsExpired(now))
            return null;

        // The account is asked every time rather than trusted at issue: expiring, locking or deleting
        // somebody has to stop their tokens on the next request, not whenever the token runs out.
        var user = await users.GetUser(token.UserId, ct);
        if (user is null || user.IsAnonymous || user.IsLockedOut || user.IsExpired(now))
            return null;

        if (token.LastUsedUtc is not { } last || now - last > LastUsedResolution)
        {
            token.LastUsedUtc = now;
            await store.Update(token, cancellationToken: ct);
        }

        return new AccessTokenMatch(token, user);
    }

    static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
