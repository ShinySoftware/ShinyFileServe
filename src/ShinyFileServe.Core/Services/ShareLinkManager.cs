using System.Security.Cryptography;
using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

/// <summary>
/// Storage for share links. Deliberately knows nothing about who may open one - that is decided
/// where the request arrives, against the creator's live rights, so there is one place to be wrong.
/// </summary>
public interface IShareLinkManager
{
    Task<ShareLink> Create(
        User creator,
        string folderId,
        string path,
        bool isDirectory,
        string title,
        ShareLinkAudience audience,
        IReadOnlyList<string> userIds,
        DateTimeOffset? expiresAtUtc,
        CancellationToken ct = default
    );

    Task<ShareLink?> Get(string id, CancellationToken ct = default);
    Task<ShareLink?> FindByToken(string token, CancellationToken ct = default);

    /// <summary>Every link, newest first. Administrators only - the endpoint decides that.</summary>
    Task<IReadOnlyList<ShareLink>> GetAll(CancellationToken ct = default);

    /// <summary>The links one account made, newest first.</summary>
    Task<IReadOnlyList<ShareLink>> GetCreatedBy(string userId, CancellationToken ct = default);

    /// <summary>The links naming one account as a recipient - what has been shared with them.</summary>
    Task<IReadOnlyList<ShareLink>> GetSharedWith(string userId, CancellationToken ct = default);

    Task Delete(string id, CancellationToken ct = default);

    /// <summary>Counts an opening of the link itself. Not called for the files fetched behind it.</summary>
    Task RecordOpen(ShareLink link, CancellationToken ct = default);
}

public class ShareLinkManager(IDocumentStore store, TimeProvider time) : IShareLinkManager
{
    public async Task<ShareLink> Create(
        User creator,
        string folderId,
        string path,
        bool isDirectory,
        string title,
        ShareLinkAudience audience,
        IReadOnlyList<string> userIds,
        DateTimeOffset? expiresAtUtc,
        CancellationToken ct = default)
    {
        var link = new ShareLink
        {
            Id = Ids.New(),
            Token = NewToken(),
            FolderId = folderId,
            Path = Normalize(path),
            IsDirectory = isDirectory,
            Title = title.Trim(),
            Audience = audience,

            // An "anyone" link that also remembered a list of names would read as restricted in
            // the list while being open to the world.
            UserIds = audience == ShareLinkAudience.Accounts ? userIds.Distinct().ToList() : [],
            CreatedByUserId = creator.Id,
            CreatedByUsername = creator.Username,
            CreatedUtc = time.GetUtcNow(),
            ExpiresAtUtc = expiresAtUtc
        };

        await store.Insert(link, cancellationToken: ct);
        return link;
    }

    public Task<ShareLink?> Get(string id, CancellationToken ct = default)
        => store.Get<ShareLink>(id, cancellationToken: ct);

    public async Task<ShareLink?> FindByToken(string token, CancellationToken ct = default)
    {
        if (String.IsNullOrWhiteSpace(token))
            return null;

        var matches = await store.Query<ShareLink>().Where(x => x.Token == token).ToList(ct);
        return matches.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ShareLink>> GetAll(CancellationToken ct = default)
        => (await store.Query<ShareLink>().ToList(ct)).OrderByDescending(x => x.CreatedUtc).ToList();

    public async Task<IReadOnlyList<ShareLink>> GetCreatedBy(string userId, CancellationToken ct = default)
        => (await store.Query<ShareLink>().Where(x => x.CreatedByUserId == userId).ToList(ct))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();

    public async Task<IReadOnlyList<ShareLink>> GetSharedWith(string userId, CancellationToken ct = default)
        => (await store.Query<ShareLink>().Where(x => x.Audience == ShareLinkAudience.Accounts).ToList(ct))
            .Where(x => x.UserIds.Contains(userId))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();

    public async Task Delete(string id, CancellationToken ct = default)
        => await store.Remove<ShareLink>(id, cancellationToken: ct);

    public async Task RecordOpen(ShareLink link, CancellationToken ct = default)
    {
        // Read again rather than writing the copy the caller holds: that copy may be from before a
        // revoke, and writing it back would bring the link back to life.
        var stored = await store.Get<ShareLink>(link.Id, cancellationToken: ct);
        if (stored is null)
            return;

        stored.OpenCount++;
        stored.LastOpenedUtc = time.GetUtcNow();
        await store.Update(stored, cancellationToken: ct);
    }

    /// <summary>128 random bits, URL-safe. Unguessable is the whole of an "anyone" link's protection.</summary>
    static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    static string Normalize(string? path)
        => String.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Trim('/');
}
