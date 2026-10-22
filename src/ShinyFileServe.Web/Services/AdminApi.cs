using System.Net;
using System.Net.Http.Json;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// Everything an administrator can change. Split from <see cref="FileServeApi"/> because it is a
/// different audience: the file calls are what every signed-in person uses, and these are what one
/// person uses occasionally.
/// </summary>
public class AdminApi(FileServeApi api)
{
    HttpClient Http => api.Http;

    // -- users --------------------------------------------------------------

    public async Task<List<UserDetail>> GetUsers(CancellationToken ct = default)
    {
        using var response = await this.Http.GetAsync("api/admin/user-details", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ListUserDetail, ct) ?? [];
    }

    public async Task<UserDetail> SaveUser(SaveUserRequest request, CancellationToken ct = default)
    {
        using var response = await this.Http.PostAsJsonAsync(
            "api/admin/users", request, ClientJsonContext.Default.SaveUserRequest, ct
        );
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.UserDetail, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The account was saved but the server sent nothing back.");
    }

    public async Task DeleteUser(string id, CancellationToken ct = default)
    {
        using var response = await this.Http.DeleteAsync($"api/admin/users/{Uri.EscapeDataString(id)}", ct);
        await Ensure(response);
    }

    /// <summary>Ends a lockout. The only way out of one - an account cannot talk itself back in.</summary>
    public async Task UnlockUser(string id, CancellationToken ct = default)
    {
        using var response = await this.Http.PostAsync($"api/admin/users/{Uri.EscapeDataString(id)}/unlock", null, ct);
        await Ensure(response);
    }

    // -- groups -------------------------------------------------------------

    public async Task<List<RoleInfo>> GetRoles(CancellationToken ct = default)
    {
        using var response = await this.Http.GetAsync("api/admin/roles", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ListRoleInfo, ct) ?? [];
    }

    public async Task<RoleInfo> SaveRole(SaveRoleRequest request, CancellationToken ct = default)
    {
        using var response = await this.Http.PostAsJsonAsync(
            "api/admin/roles", request, ClientJsonContext.Default.SaveRoleRequest, ct
        );
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.RoleInfo, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The group was saved but the server sent nothing back.");
    }

    public async Task DeleteRole(string id, CancellationToken ct = default)
    {
        using var response = await this.Http.DeleteAsync($"api/admin/roles/{Uri.EscapeDataString(id)}", ct);
        await Ensure(response);
    }

    // -- package feeds ------------------------------------------------------

    /// <summary>Every access token on the server, or only one account's.</summary>
    public async Task<List<AdminAccessTokenInfo>> GetAccessTokens(string? userId = null, CancellationToken ct = default)
    {
        var url = String.IsNullOrEmpty(userId)
            ? "api/admin/packages/tokens"
            : $"api/admin/packages/tokens?userId={Uri.EscapeDataString(userId)}";

        using var response = await this.Http.GetAsync(url, ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ListAdminAccessTokenInfo, ct) ?? [];
    }

    /// <summary>Revokes anybody's token.</summary>
    public async Task RevokeAccessToken(string id, CancellationToken ct = default)
    {
        using var response = await this.Http.DeleteAsync($"api/admin/packages/tokens/{Uri.EscapeDataString(id)}", ct);
        await Ensure(response);
    }

    public async Task<PagedResponse<PackageRequestInfo>> GetPackageTraffic(PackageTrafficQuery query, CancellationToken ct = default)
    {
        var parts = new List<string>
        {
            $"page={query.Page}",
            $"pageSize={query.PageSize}"
        };

        if (!String.IsNullOrEmpty(query.UserId))
            parts.Add($"userId={Uri.EscapeDataString(query.UserId)}");
        if (!String.IsNullOrEmpty(query.TokenId))
            parts.Add($"tokenId={Uri.EscapeDataString(query.TokenId)}");
        if (query.Feed is { } feed)
            parts.Add($"feed={(int)feed}");
        if (query.Operation is { } operation)
            parts.Add($"operation={(int)operation}");
        if (!String.IsNullOrWhiteSpace(query.PackageId))
            parts.Add($"packageId={Uri.EscapeDataString(query.PackageId.Trim())}");
        if (query.RefusedOnly)
            parts.Add("refusedOnly=true");
        if (query.FromUtc is { } from)
            parts.Add($"fromUtc={Uri.EscapeDataString(from.ToString("O"))}");
        if (query.ToUtc is { } to)
            parts.Add($"toUtc={Uri.EscapeDataString(to.ToString("O"))}");

        using var response = await this.Http.GetAsync($"api/admin/packages/traffic?{String.Join("&", parts)}", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.PagedResponsePackageRequestInfo, ct)
            ?? new PagedResponse<PackageRequestInfo>([], 0, 1, query.PageSize);
    }

    // -- shares -------------------------------------------------------------

    public async Task<List<ShareInfo>> GetShares(CancellationToken ct = default)
    {
        using var response = await this.Http.GetAsync("api/admin/shares", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ListShareInfo, ct) ?? [];
    }

    public async Task<ShareDetail> GetShare(string id, CancellationToken ct = default)
    {
        using var response = await this.Http.GetAsync($"api/admin/shares/{Uri.EscapeDataString(id)}", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ShareDetail, ct)
            ?? throw new ApiException(HttpStatusCode.NotFound, "That share is no longer there.");
    }

    public async Task<ShareDetail> SaveShare(SaveShareRequest request, CancellationToken ct = default)
    {
        using var response = await this.Http.PostAsJsonAsync(
            "api/admin/shares", request, ClientJsonContext.Default.SaveShareRequest, ct
        );
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ShareDetail, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The share was saved but the server sent nothing back.");
    }

    public async Task DeleteShare(string id, CancellationToken ct = default)
    {
        using var response = await this.Http.DeleteAsync($"api/admin/shares/{Uri.EscapeDataString(id)}", ct);
        await Ensure(response);
    }

    // -- the machine's own folders ------------------------------------------

    /// <summary>
    /// The folders inside <paramref name="path"/> on the machine running the server. A null or
    /// empty path asks for the starting points rather than a directory.
    /// </summary>
    public async Task<MachineFolderListing> Browse(string? path = null, bool showHidden = false, CancellationToken ct = default)
    {
        var query = $"api/admin/browse?showHidden={(showHidden ? "true" : "false")}";
        if (!String.IsNullOrEmpty(path))
            query += $"&path={Uri.EscapeDataString(path)}";

        using var response = await this.Http.GetAsync(query, ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.MachineFolderListing, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The server sent back no folders.");
    }

    public async Task<MachineFolder> CreateMachineFolder(string parent, string name, CancellationToken ct = default)
    {
        using var response = await this.Http.PostAsJsonAsync(
            "api/admin/browse/directory",
            new CreateMachineFolderRequest(parent, name),
            ClientJsonContext.Default.CreateMachineFolderRequest,
            ct
        );
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.MachineFolder, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The folder was made but the server sent nothing back.");
    }

    // -- what is happening right now ----------------------------------------

    /// <summary>
    /// Who is here and what is moving, at this instant. Re-asked about once a second while the live
    /// screen is open, so it stays a single small round trip on purpose.
    /// </summary>
    public async Task<LiveActivitySnapshot> GetLiveActivity(CancellationToken ct = default)
    {
        using var response = await this.Http.GetAsync("api/admin/live", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.LiveActivitySnapshot, ct)
            ?? new LiveActivitySnapshot([], [], [], BandwidthNow.Empty(DateTimeOffset.UtcNow));
    }

    /// <summary>How much has gone through the server over a window, by period and by account.</summary>
    public async Task<BandwidthReport> GetBandwidth(BandwidthQuery query, CancellationToken ct = default)
    {
        var parts = new List<string> { $"bucket={(int)query.Bucket}" };

        if (!String.IsNullOrEmpty(query.UserId))
            parts.Add($"userId={Uri.EscapeDataString(query.UserId)}");
        if (query.FromUtc is { } from)
            parts.Add($"fromUtc={Uri.EscapeDataString(from.ToString("O"))}");
        if (query.ToUtc is { } to)
            parts.Add($"toUtc={Uri.EscapeDataString(to.ToString("O"))}");

        using var response = await this.Http.GetAsync($"api/admin/bandwidth?{String.Join("&", parts)}", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.BandwidthReport, ct)
            ?? BandwidthReport.Empty(query.FromUtc ?? DateTimeOffset.UtcNow, query.ToUtc ?? DateTimeOffset.UtcNow, query.Bucket);
    }

    // -- backup -------------------------------------------------------------

    /// <summary>The href a browser follows to download a backup. It streams, so it is a navigation.</summary>
    public string BackupUrl => "api/admin/backup";

    public async Task<BackupSummary> StageBackup(string fileName, Stream content, long length, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var part = new StreamContent(content);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        part.Headers.ContentLength = length;
        form.Add(part, "backup", fileName);

        using var response = await this.Http.PostAsync("api/admin/backup/stage", form, ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.BackupSummary, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The server read the backup but sent nothing back.");
    }

    public async Task RestoreStaged(CancellationToken ct = default)
    {
        using var response = await this.Http.PostAsync("api/admin/backup/restore", null, ct);
        await Ensure(response);
    }

    public async Task DiscardStaged(CancellationToken ct = default)
    {
        using var response = await this.Http.DeleteAsync("api/admin/backup/stage", ct);
        await Ensure(response);
    }

    // -- app updates --------------------------------------------------------

    public async Task<AppUpdateInfo> GetUpdate(CancellationToken ct = default)
    {
        using var response = await this.Http.GetAsync("api/admin/update", ct);
        await Ensure(response);
        return await ReadUpdate(response, ct);
    }

    /// <summary>Asks GitHub again rather than reading back what the server last heard.</summary>
    public async Task<AppUpdateInfo> CheckForUpdate(CancellationToken ct = default)
    {
        using var response = await this.Http.PostAsync("api/admin/update/check", null, ct);
        await Ensure(response);
        return await ReadUpdate(response, ct);
    }

    public async Task<AppUpdateInfo> SkipUpdate(bool skip, CancellationToken ct = default)
    {
        using var response = await this.Http.PutAsJsonAsync(
            "api/admin/update/skip", new SkipAppUpdateRequest(skip), ClientJsonContext.Default.SkipAppUpdateRequest, ct
        );
        await Ensure(response);
        return await ReadUpdate(response, ct);
    }

    /// <summary>Starts the download. Progress is <see cref="GetUpdate"/>, until the server goes away to restart.</summary>
    public async Task<AppUpdateInfo> InstallUpdate(CancellationToken ct = default)
    {
        using var response = await this.Http.PostAsync("api/admin/update/install", null, ct);
        await Ensure(response);
        return await ReadUpdate(response, ct);
    }

    static async Task<AppUpdateInfo> ReadUpdate(HttpResponseMessage response, CancellationToken ct)
        => await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.AppUpdateInfo, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The server sent nothing back about updates.");

    static async Task Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = (await response.Content.ReadAsStringAsync()).Trim().Trim('"');
        throw new ApiException(
            response.StatusCode,
            body.Length is > 0 and < 240 && !body.StartsWith('{') && !body.StartsWith('<')
                ? body
                : $"The server returned {(int)response.StatusCode}."
        );
    }
}
