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
