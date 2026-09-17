using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.AppDeviceBridge.Tunnel;
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
    AppDeviceBridgeTunnel tunnel,
    ISettingsManager settings,
    IFolderManager folders,
    IUserManager users,
    ICurrentUserResolver current,
    IActivityLogger activity,
    IStartupService startup,
    ILogger<AdminEndpoints> logger
)
{
    [Get("/status")]
    [Produces(200, typeof(ServerStatus))]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        await settings.Load(ct);
        return new OkObjectResult(await this.BuildStatus(ct));
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

        // Not part of the settings document, so it is handed to the machine rather than saved - see
        // RunAtStartupState for why the machine keeps this one rather than the database. Done after
        // the save because it is the only setting on this form that can refuse, and a refusal should
        // not take the rest of it down with it.
        if (request.RunAtStartup is { } wanted && wanted != (await this.ReadStartup(ct) is RunAtStartupState.Enabled))
        {
            var (state, problem) = await this.ApplyStartup(wanted, ct);
            var took = (state is RunAtStartupState.Enabled) == wanted;
            var why = problem ?? RunAtStartupText.Explain(state);

            await this.LogAdminAction(
                ctx,
                took
                    ? $"Turned {(wanted ? "on" : "off")} starting at login"
                    : $"Could not turn {(wanted ? "on" : "off")} starting at login: {why}",
                ct
            );

            // Register can succeed and still leave the app not starting - macOS answers
            // RequiresApproval until the person allows it in System Settings - so the message is
            // the state's own explanation rather than the word "failed".
            if (!took)
                return new OkObjectResult((await this.BuildStatus(ct)) with { StateText = $"Saved. {why}" });
        }

        // Changing the port while answering a request on the old one would kill this very response,
        // so the rebind is deliberately deferred to an explicit restart from the caller.
        if (portChanged)
            return new OkObjectResult((await this.BuildStatus(ct)) with { StateText = "Restart the server to move to the new port" });

        return new OkObjectResult(await this.BuildStatus(ct));
    }

    [Post("/server/start")]
    [Produces(200, typeof(ServerStatus))]
    public async Task<IActionResult> StartServer(HttpContext ctx, CancellationToken ct)
    {
        await server.Start(ct);
        await this.LogAdminAction(ctx, "Started the server", ct);
        return new OkObjectResult(await this.BuildStatus(ct));
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
            await tunnel.StopAsync();
            await server.Stop();
        }, CancellationToken.None);

        return new StatusCodeResult(StatusCodes.Status202Accepted);
    }

    [Post("/tunnel/start")]
    [Produces(200, typeof(ServerStatus))]
    [Produces(409)]
    public async Task<IActionResult> StartTunnel(HttpContext ctx, CancellationToken ct)
    {
        // The tunnel could serve a stopped server, but here the server switch is meant to take all of it down.
        if (!server.IsRunning)
            return new ConflictObjectResult("Start the server before opening a tunnel to it.");

        // A blank pinggy token gives an anonymous tunnel: it expires after an hour and takes a new address on every
        // reconnect. A failure is not an error here - the status below carries it.
        var url = await tunnel.StartAsync(settings.Current.PinggyToken, ct);

        await this.LogAdminAction(ctx, $"Opened the public tunnel ({url?.GetLeftPart(UriPartial.Authority) ?? "no address"})", ct);
        return new OkObjectResult(await this.BuildStatus(ct));
    }

    [Post("/tunnel/stop")]
    [Produces(200, typeof(ServerStatus))]
    public async Task<IActionResult> StopTunnel(HttpContext ctx, CancellationToken ct)
    {
        await tunnel.StopAsync(ct);
        await this.LogAdminAction(ctx, "Closed the public tunnel", ct);
        return new OkObjectResult(await this.BuildStatus(ct));
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

    async Task<ServerStatus> BuildStatus(CancellationToken ct)
    {
        var config = settings.Current;

        // Asked every time rather than remembered: this one lives on the machine, and the operator
        // may have removed it from outside this app since the last look. It is a registry read or a
        // File.Exists, and /status is fetched when the settings screen opens rather than polled.
        var launch = await this.ReadStartup(ct);

        return new ServerStatus(
            server.IsRunning,
            server.State.ToString(),
            NetworkAddresses.LocalUrls(config.Port),
            server.ActiveConnections,
            tunnel.State switch
            {
                TunnelState.Connecting => TunnelStatus.Connecting,
                TunnelState.Connected => TunnelStatus.Open,
                TunnelState.Reconnecting => TunnelStatus.Reconnecting,
                TunnelState.Failed => TunnelStatus.Failed,
                _ => TunnelStatus.Off
            },
            tunnel.PublicUrl?.GetLeftPart(UriPartial.Authority),
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
                Math.Max(0, config.MaxFailedSignIns),
                launch
            )
        );
    }

    /// <summary>
    /// The platform's own answer, or <see cref="RunAtStartupState.NotSupported"/> where it could not
    /// be had. Guarded because the read is a registry open or a launch-services call and a host that
    /// cannot locate its own executable - a single-file build under a debugger - throws rather than
    /// answering. /status carries the whole settings screen, and one switch nobody can read must not
    /// take the rest of it down.
    /// </summary>
    async Task<RunAtStartupState> ReadStartup(CancellationToken ct)
    {
        if (!startup.IsSupported)
            return RunAtStartupState.NotSupported;

        try
        {
            return (RunAtStartupState)(int)await startup.GetState(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read whether the app starts at login");
            return RunAtStartupState.NotSupported;
        }
    }

    /// <summary>
    /// Turns it on or off and reports what was actually achieved, which is not always what was
    /// asked for. On a throw the state is re-read rather than the request echoed back: a write that
    /// failed half way still may have landed, and the operator is owed the machine's answer.
    /// </summary>
    async Task<(RunAtStartupState State, string? Problem)> ApplyStartup(bool enabled, CancellationToken ct)
    {
        try
        {
            var state = enabled ? await startup.Register(ct) : await startup.Unregister(ct);
            return ((RunAtStartupState)(int)state, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not change whether the app starts at login");
            return (await this.ReadStartup(ct), ex.Message);
        }
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
