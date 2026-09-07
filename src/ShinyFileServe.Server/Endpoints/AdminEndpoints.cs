using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// Server, tunnel and settings control for anyone whose account or group is marked as an
/// administrator. This is the same surface the desktop app drives, so the two can never diverge.
/// </summary>
[Route("/api/admin")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminEndpoints(
    IFileServeServer server,
    ITunnelController tunnel,
    ISettingsManager settings,
    IFolderManager folders,
    IUserManager users,
    ICurrentUserResolver current,
    IActivityLogger activity
)
{
    [Get("/status")]
    [Produces(200, typeof(ServerStatus))]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        await settings.Load(ct);
        return new OkObjectResult(this.BuildStatus());
    }

    [Put("/settings")]
    [Produces(200, typeof(ServerStatus))]
    [Produces(400)]
    public async Task<IActionResult> UpdateSettings(UpdateServerConfig request, HttpContext ctx, CancellationToken ct)
    {
        if (request.Port is < 1 or > 65535)
            return new BadRequestObjectResult("Port must be between 1 and 65535.");

        if (request.RememberMeDays is < 1 or > 3650)
            return new BadRequestObjectResult("A remembered session must last between 1 and 3650 days.");

        if (request.IdleLogoffMinutes is < 0 or > 1440)
            return new BadRequestObjectResult("Idle sign-out must be between 0 and 1440 minutes. 0 turns it off.");

        if (request.MaxFailedSignIns is < 0 or > 100)
            return new BadRequestObjectResult("Lock an account after between 1 and 100 failed sign-ins. 0 never locks one.");

        var stored = await settings.Load(ct);
        var portChanged = stored.Port != request.Port;

        stored.Port = request.Port;
        stored.AutoStartServer = request.AutoStartServer;
        stored.AutoStartTunnel = request.AutoStartTunnel;

        // Null means "leave it as it is" - the client never receives the token, so it cannot
        // echo one back. An empty string is an explicit clear.
        if (request.PinggyToken is not null)
            stored.PinggyToken = request.PinggyToken.Length == 0 ? null : request.PinggyToken.Trim();

        stored.AllowRememberMe = request.AllowRememberMe;
        stored.RememberMeDays = request.RememberMeDays;
        stored.IdleLogoffMinutes = request.IdleLogoffMinutes;
        if (request.Passwords is { } rules)
        {
            if (rules.MinimumLength is < 1 or > 128)
                return new BadRequestObjectResult("The minimum password length must be between 1 and 128.");

            if (rules.MaximumAgeDays is < 0 or > 3650)
                return new BadRequestObjectResult("A password may last between 1 and 3650 days. 0 never expires one.");

            stored.PasswordRules = PasswordPolicyMapping.ToRules(rules);
        }

        // Null leaves the stored number alone, the same way a null token does - 0 is a real value
        // here and means nobody is ever locked out.
        if (request.MaxFailedSignIns is { } lockout)
            stored.MaxFailedSignIns = lockout;

        stored.NotificationsEnabled = request.NotificationsEnabled;
        if (request.NotifyOn is not null)
            stored.NotifyOn = request.NotifyOn.Distinct().ToList();

        await settings.Save(stored, ct);
        await this.LogAdminAction(ctx, "Changed server settings", ct);

        // Changing the port while answering a request on the old one would kill this very response,
        // so the rebind is deliberately deferred to an explicit restart from the caller.
        if (portChanged)
            return new OkObjectResult(this.BuildStatus() with { StateText = "Restart the server to move to the new port" });

        return new OkObjectResult(this.BuildStatus());
    }

    [Post("/server/start")]
    [Produces(200, typeof(ServerStatus))]
    public async Task<IActionResult> StartServer(HttpContext ctx, CancellationToken ct)
    {
        await server.Start(ct);
        await this.LogAdminAction(ctx, "Started the server", ct);
        return new OkObjectResult(this.BuildStatus());
    }

    /// <summary>
    /// Stopping is answered before it happens - the response has to leave through the listener that
    /// is about to close, so the stop is scheduled rather than awaited.
    /// </summary>
    [Post("/server/stop")]
    [Produces(202)]
    public async Task<IActionResult> StopServer(HttpContext ctx, CancellationToken ct)
    {
        await this.LogAdminAction(ctx, "Stopped the server", ct);
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            await tunnel.Stop();
            await server.Stop();
        }, CancellationToken.None);

        return new StatusCodeResult(StatusCodes.Status202Accepted);
    }

    [Post("/tunnel/start")]
    [Produces(200, typeof(ServerStatus))]
    [Produces(409)]
    public async Task<IActionResult> StartTunnel(HttpContext ctx, CancellationToken ct)
    {
        try
        {
            await tunnel.Start(ct);
        }
        catch (InvalidOperationException ex)
        {
            return new ConflictObjectResult(ex.Message);
        }

        await this.LogAdminAction(ctx, $"Opened the public tunnel ({tunnel.PublicUrl ?? "no address"})", ct);
        return new OkObjectResult(this.BuildStatus());
    }

    [Post("/tunnel/stop")]
    [Produces(200, typeof(ServerStatus))]
    public async Task<IActionResult> StopTunnel(HttpContext ctx, CancellationToken ct)
    {
        await tunnel.Stop(ct);
        await this.LogAdminAction(ctx, "Closed the public tunnel", ct);
        return new OkObjectResult(this.BuildStatus());
    }

    /// <summary>
    /// Everyone with an account, for the activity log's "who" filter. Deliberately a summary -
    /// the browser has no business receiving password hashes to populate a dropdown.
    /// </summary>
    [Get("/users")]
    [Produces(200, typeof(IReadOnlyList<UserSummary>))]
    public async Task<IActionResult> Users(CancellationToken ct)
    {
        var all = await users.GetUsers(ct);
        return new OkObjectResult(all
            .Select(u => new UserSummary(u.Id, u.Username, u.DisplayName, u.ExpiresAtUtc, u.IsAdmin, u.IsProtected))
            .ToList()
        );
    }

    /// <summary>Names or annotates one path inside a share - a file or a folder.</summary>
    [Put("/annotation")]
    [Produces(204)]
    [Produces(404)]
    public async Task<IActionResult> SetAnnotation(SetAnnotationRequest request, HttpContext ctx, CancellationToken ct)
    {
        if (await folders.GetFolder(request.FolderId, ct) is null)
            return new NotFoundResult();

        await folders.SetAnnotation(request.FolderId, request.Path, request.DisplayName, request.Description, ct);

        var detail = String.IsNullOrWhiteSpace(request.DisplayName) && String.IsNullOrWhiteSpace(request.Description)
            ? $"Cleared the note on '{request.Path}'"
            : $"Annotated '{request.Path}'";

        await this.LogAdminAction(ctx, detail, ct);
        return new NoContentResult();
    }

    // -----------------------------------------------------------------------

    ServerStatus BuildStatus()
    {
        var config = settings.Current;
        return new ServerStatus(
            server.IsRunning,
            server.State.ToString(),
            NetworkAddresses.LocalUrls(config.Port),
            server.ActiveConnections,
            (TunnelStatus)(int)tunnel.State,
            tunnel.PublicUrl,
            tunnel.LastError,
            new ServerConfig(
                config.Port,
                config.AutoStartServer,
                config.AutoStartTunnel,
                !String.IsNullOrWhiteSpace(config.PinggyToken),
                config.NotificationsEnabled,
                config.NotifyOn,
                config.AllowRememberMe,
                config.RememberMeDays,
                config.IdleLogoffMinutes,
                PasswordPolicyMapping.ToInfo(config.PasswordRules),
                Math.Max(0, config.MaxFailedSignIns)
            )
        );
    }

    async Task LogAdminAction(HttpContext ctx, string detail, CancellationToken ct)
    {
        if (await current.Resolve(ctx, ct) is not { } user)
            return;

        await activity.Log(
            ActivityAction.Login, user.Id, user.Username, success: true,
            detail: detail, context: current.GetActivityContext(ctx), ct: ct
        );
    }
}
