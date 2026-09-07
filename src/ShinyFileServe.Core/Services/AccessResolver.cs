using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

/// <summary>The effective rights of one user in one folder, with the physical root resolved.</summary>
public record FolderAccess(
    ShareFolder Folder,
    FolderPermissions Permissions,
    QuotaWindow DownloadWindow,
    long? DownloadLimit,
    QuotaWindow UploadWindow,
    long? UploadLimit,
    long? MaxUploadFileSizeBytes
);

public interface IAccessResolver
{
    /// <summary>Every folder the user can at least see, with their effective rights folded in.</summary>
    Task<IReadOnlyList<FolderAccess>> GetAccessibleFolders(User user, CancellationToken ct = default);

    /// <summary>The user's rights in one folder, or null when the folder is disabled, missing, or invisible to them.</summary>
    Task<FolderAccess?> GetAccess(User user, string folderId, CancellationToken ct = default);

    /// <summary>Renders access + live quota consumption into the shape the frontend binds to.</summary>
    Task<FolderInfo> ToFolderInfo(User user, FolderAccess access, CancellationToken ct = default);
}

public class AccessResolver(IFolderManager folders, IUserManager users, IQuotaTracker quotas, TimeProvider time) : IAccessResolver
{
    public async Task<IReadOnlyList<FolderAccess>> GetAccessibleFolders(User user, CancellationToken ct = default)
    {
        var all = await folders.GetFolders(ct);
        var grants = await folders.GetGrants(ct);
        var isAdmin = user.IsAdministrator(await users.GetRolesFor(user, ct));
        var results = new List<FolderAccess>();

        foreach (var folder in all.Where(x => x.IsEnabled))
        {
            var access = Resolve(user, isAdmin, folder, grants);
            if (access is not null && access.Permissions.Has(FolderPermissions.List))
                results.Add(access);
        }
        return results;
    }

    public async Task<FolderAccess?> GetAccess(User user, string folderId, CancellationToken ct = default)
    {
        var folder = await folders.GetFolder(folderId, ct);
        if (folder is null || !folder.IsEnabled)
            return null;

        var grants = await folders.GetGrantsForFolder(folderId, ct);
        var isAdmin = user.IsAdministrator(await users.GetRolesFor(user, ct));
        return Resolve(user, isAdmin, folder, grants);
    }

    public async Task<FolderInfo> ToFolderInfo(User user, FolderAccess access, CancellationToken ct = default)
    {
        var download = await this.BuildQuota(user, access.Folder.Id, access.DownloadWindow, access.DownloadLimit, download: true, ct);
        var upload = await this.BuildQuota(user, access.Folder.Id, access.UploadWindow, access.UploadLimit, download: false, ct);

        return new FolderInfo(
            access.Folder.Id,
            access.Folder.Name,
            access.Folder.Description,
            access.Permissions,
            download,
            upload,
            access.MaxUploadFileSizeBytes
        );
    }

    async Task<QuotaState> BuildQuota(User user, string folderId, QuotaWindow window, long? limit, bool download, CancellationToken ct)
    {
        if (window == QuotaWindow.None || limit is null)
            return new QuotaState(QuotaWindow.None, null, 0, null);

        var usage = await quotas.GetUsage(user.Id, folderId, window, ct);
        var (_, _, end) = QuotaTracker.Period(window, time.GetUtcNow());
        return new QuotaState(window, limit, download ? usage.DownloadedBytes : usage.UploadedBytes, end);
    }

    /// <summary>
    /// Folds the grants for every role the user holds into one set of rights. Permissions are a union;
    /// quotas take the most generous value, which keeps "add a role" a strictly additive act - a user
    /// can never lose access by being added to a group.
    /// </summary>
    internal static FolderAccess? Resolve(User user, bool isAdmin, ShareFolder folder, IReadOnlyList<FolderGrant> grants)
    {
        if (isAdmin)
        {
            var all = FolderPermissions.List | FolderPermissions.DownloadFile | FolderPermissions.CreateFile
                | FolderPermissions.DeleteFile | FolderPermissions.CreateDirectory;
            return new FolderAccess(folder, all, QuotaWindow.None, null, QuotaWindow.None, null, null);
        }

        var applicable = grants
            .Where(g => g.FolderId == folder.Id && user.RoleIds.Contains(g.RoleId))
            .ToList();

        if (applicable.Count == 0)
            return null;

        var permissions = applicable.Aggregate(FolderPermissions.None, (acc, g) => acc | g.Permissions);
        if (permissions == FolderPermissions.None)
            return null;

        var (downloadWindow, downloadLimit) = MostGenerous(applicable, g => (g.DownloadQuotaWindow, g.DownloadQuotaBytes));
        var (uploadWindow, uploadLimit) = MostGenerous(applicable, g => (g.UploadQuotaWindow, g.UploadQuotaBytes));

        // a grant with no per-file cap means "no cap", so one unrestricted role lifts the ceiling entirely
        var maxFile = applicable.Any(g => g.MaxUploadFileSizeBytes is null)
            ? (long?)null
            : applicable.Max(g => g.MaxUploadFileSizeBytes);

        return new FolderAccess(folder, permissions, downloadWindow, downloadLimit, uploadWindow, uploadLimit, maxFile);
    }

    static (QuotaWindow Window, long? Limit) MostGenerous(
        List<FolderGrant> grants,
        Func<FolderGrant, (QuotaWindow, long?)> selector)
    {
        var quotas = grants.Select(selector).ToList();

        // any grant without a quota wins outright - the user is unmetered through that role
        if (quotas.Any(q => q.Item1 == QuotaWindow.None || q.Item2 is null))
            return (QuotaWindow.None, null);

        // otherwise keep the largest allowance; a tie across different windows keeps the first,
        // which is deterministic because grants come back ordered by the store
        var best = quotas.OrderByDescending(q => q.Item2!.Value).First();
        return best;
    }
}
