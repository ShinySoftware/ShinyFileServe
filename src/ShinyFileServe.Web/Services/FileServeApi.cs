using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Web.Services;

/// <summary>Raised when the server answers with something the caller should show the user verbatim.</summary>
public class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public bool IsUnauthorized => this.Status == HttpStatusCode.Unauthorized;
    public bool IsForbidden => this.Status == HttpStatusCode.Forbidden;
    public bool IsQuotaExceeded => this.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestEntityTooLarge;
}

public class FileServeApi(HttpClient http)
{
    public HttpClient Http => http;

    // -- auth ---------------------------------------------------------------

    /// <summary>Session rules. Public - the sign-in page needs them before anyone has signed in.</summary>
    public async Task<SessionPolicy> GetSessionPolicy(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/auth/policy", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.SessionPolicy, ct)
            ?? new SessionPolicy(false, false, true, 30, 0, PasswordPolicyInfo.Off, []);
    }

    /// <summary>Creates the first administrator and signs them in.</summary>
    public async Task<CurrentUser> CompleteSetup(string username, string password, string displayName, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/auth/setup",
            new SetupRequest(username, password, displayName),
            ClientJsonContext.Default.SetupRequest,
            ct
        );
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.CurrentUser, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The account was created but the server sent nothing back.");
    }

    public async Task<CurrentUser?> GetCurrentUser(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/auth/me", ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.CurrentUser, ct);
    }

    public async Task<CurrentUser> SignIn(string username, string password, bool remember, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/auth/login",
            new LoginRequest(username, password, remember),
            ClientJsonContext.Default.LoginRequest,
            ct
        );

        // A bare 401 is the deliberately vague refusal; one with a body has something specific to
        // say - an expired account - and that message is the server's to write, not this method's.
        if (response.StatusCode == HttpStatusCode.Unauthorized
            && (await response.Content.ReadAsStringAsync(ct)).Trim().Length == 0)
        {
            throw new ApiException(HttpStatusCode.Unauthorized, "That username and password do not match an account.");
        }

        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.CurrentUser, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The server signed you in but sent nothing back.");
    }

    /// <summary>
    /// Signs in as one of the accounts the policy named as anonymous. There is no password to send,
    /// and the server refuses any id that is not one of them.
    /// </summary>
    public async Task<CurrentUser> SignInAnonymous(string userId, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/auth/anonymous",
            new AnonymousSignInRequest(userId),
            ClientJsonContext.Default.AnonymousSignInRequest,
            ct
        );

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ApiException(HttpStatusCode.Unauthorized, "That guest account is no longer available.");

        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.CurrentUser, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The server signed you in but sent nothing back.");
    }

    public async Task SignOut(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("api/auth/logout", null, ct);
        await Ensure(response);
    }

    // -- your own account ---------------------------------------------------

    public async Task<AccountProfile> GetProfile(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/account/profile", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.AccountProfile, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The server sent back no account.");
    }

    public async Task ChangePassword(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/account/password",
            new ChangePasswordRequest(currentPassword, newPassword),
            ClientJsonContext.Default.ChangePasswordRequest,
            ct
        );
        await Ensure(response);
    }

    // -- access tokens ------------------------------------------------------

    public async Task<List<AccessTokenInfo>> GetTokens(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/account/tokens", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ListAccessTokenInfo, ct) ?? [];
    }

    /// <summary>The one call whose answer carries a token's secret. Show it, then forget it.</summary>
    public async Task<CreatedAccessToken> CreateToken(string name, int? expiresInDays, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/account/tokens",
            new CreateAccessTokenRequest(name, expiresInDays),
            ClientJsonContext.Default.CreateAccessTokenRequest,
            ct
        );
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.CreatedAccessToken, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The token was made but the server sent nothing back.");
    }

    public async Task RevokeToken(string id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/account/tokens/{Uri.EscapeDataString(id)}", ct);
        await Ensure(response);
    }

    // -- folders ------------------------------------------------------------

    public async Task<List<FolderInfo>> GetFolders(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/folders", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ListFolderInfo, ct) ?? [];
    }

    public async Task<FolderInfo?> GetFolder(string folderId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/folders/{Uri.EscapeDataString(folderId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.FolderInfo, ct);
    }

    // -- files --------------------------------------------------------------

    public async Task<DirectoryListing> List(string folderId, string? path, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/files?{Query(folderId, path)}", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.DirectoryListing, ct)
            ?? new DirectoryListing(folderId, path ?? "", []);
    }

    /// <summary>The href a browser follows to download. The session cookie rides along on its own.</summary>
    public string DownloadUrl(string folderId, string path)
        => $"api/files/download?{Query(folderId, path)}";

    public async Task Delete(string folderId, string path, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/files?{Query(folderId, path)}", ct);
        await Ensure(response);
    }

    public async Task<FileEntryInfo> CreateDirectory(string folderId, string? path, string name, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/files/directory",
            new CreateDirectoryRequest(folderId, path ?? "", name),
            ClientJsonContext.Default.CreateDirectoryRequest,
            ct
        );
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.FileEntryInfo, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The folder was created but the server sent nothing back.");
    }

    public async Task<List<FileEntryInfo>> Upload(
        string folderId,
        string? path,
        IReadOnlyList<(string Name, Stream Content, long Length)> files,
        bool overwrite,
        CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        foreach (var (name, stream, length) in files)
        {
            var part = new StreamContent(stream);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            part.Headers.ContentLength = length;
            content.Add(part, "files", name);
        }

        var url = $"api/files/upload?{Query(folderId, path)}&overwrite={(overwrite ? "true" : "false")}";
        using var response = await http.PostAsync(url, content, ct);
        await Ensure(response);

        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ListFileEntryInfo, ct) ?? [];
    }

    // -- administration -------------------------------------------------------

    public async Task<PagedResponse<ActivityEntryInfo>> GetActivity(ActivityQuery query, CancellationToken ct = default)
    {
        var parts = new List<string>
        {
            $"page={query.Page}",
            $"pageSize={query.PageSize}",
            $"sortBy={(int)query.SortBy}",
            $"descending={(query.Descending ? "true" : "false")}"
        };

        if (!String.IsNullOrEmpty(query.UserId))
            parts.Add($"userId={Uri.EscapeDataString(query.UserId)}");
        if (!String.IsNullOrEmpty(query.FolderId))
            parts.Add($"folderId={Uri.EscapeDataString(query.FolderId)}");
        if (query.Action is { } action)
            parts.Add($"action={(int)action}");
        if (query.FromUtc is { } from)
            parts.Add($"fromUtc={Uri.EscapeDataString(from.ToString("O"))}");
        if (query.ToUtc is { } to)
            parts.Add($"toUtc={Uri.EscapeDataString(to.ToString("O"))}");

        using var response = await http.GetAsync($"api/activity?{String.Join("&", parts)}", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.PagedResponseActivityEntryInfo, ct)
            ?? new PagedResponse<ActivityEntryInfo>([], 0, 1, query.PageSize);
    }

    public async Task<List<UserSummary>> GetUsers(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/admin/users", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ListUserSummary, ct) ?? [];
    }

    public async Task<ServerStatus> GetServerStatus(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/admin/status", ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ServerStatus, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The server sent no status back.");
    }

    public async Task<ServerStatus> SaveServerConfig(UpdateServerConfig config, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync(
            "api/admin/settings", config, ClientJsonContext.Default.UpdateServerConfig, ct
        );
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ServerStatus, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The server sent no status back.");
    }

    /// <summary>
    /// Stopping the server answers 202 and then closes the listener the response left through, so
    /// there is no status to read back - the caller shows "stopping" and stops polling.
    /// </summary>
    public async Task<ServerStatus?> ControlServer(bool start, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/admin/server/{(start ? "start" : "stop")}", null, ct);
        await Ensure(response);

        return response.StatusCode == HttpStatusCode.Accepted
            ? null
            : await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ServerStatus, ct);
    }

    public async Task<ServerStatus> ControlTunnel(bool start, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/admin/tunnel/{(start ? "start" : "stop")}", null, ct);
        await Ensure(response);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ServerStatus, ct)
            ?? throw new ApiException(HttpStatusCode.InternalServerError, "The server sent no status back.");
    }

    public async Task SetAnnotation(string folderId, string path, string? displayName, string? description, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync(
            "api/admin/annotation",
            new SetAnnotationRequest(folderId, path, displayName, description),
            ClientJsonContext.Default.SetAnnotationRequest,
            ct
        );
        await Ensure(response);
    }

    // -- plumbing -----------------------------------------------------------

    static string Query(string folderId, string? path)
    {
        var query = $"folderId={Uri.EscapeDataString(folderId)}";
        return String.IsNullOrEmpty(path) ? query : $"{query}&path={Uri.EscapeDataString(path)}";
    }

    static async Task Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        throw new ApiException(response.StatusCode, Describe(response.StatusCode, body));
    }

    /// <summary>
    /// Server messages are terse and status-shaped; turn them into something a person can act on.
    /// </summary>
    static string Describe(HttpStatusCode status, string body)
    {
        var trimmed = body.Trim().Trim('"');
        if (!String.IsNullOrWhiteSpace(trimmed) && trimmed.Length < 200 && !trimmed.StartsWith('{') && !trimmed.StartsWith('<'))
            return trimmed;

        return status switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Sign in again.",
            HttpStatusCode.Forbidden => "You do not have permission to do that here.",
            HttpStatusCode.NotFound => "That folder or file is no longer there.",
            HttpStatusCode.Conflict => "Something with that name already exists.",
            HttpStatusCode.RequestEntityTooLarge => "That file is larger than this folder allows.",
            HttpStatusCode.TooManyRequests => "You have used up your quota for this folder.",
            HttpStatusCode.UnprocessableEntity => "That file was refused by the virus scanner.",
            HttpStatusCode.ServiceUnavailable => "That file could not be scanned, so it was not accepted.",
            _ => $"The server returned {(int)status}."
        };
    }
}
