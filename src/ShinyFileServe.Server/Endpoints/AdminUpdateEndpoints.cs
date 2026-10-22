using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Server.Updates;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// New releases of the app. Administrator-only like the rest of the server's controls: installing
/// one restarts the server, which is no more than Stop already lets an administrator do.
/// </summary>
[Route("/api/admin/update")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminUpdateEndpoints(
    IAppUpdater updater,
    ICurrentUserResolver current,
    IActivityLogger activity
)
{
    [Get]
    [Produces(200, typeof(AppUpdateInfo))]
    public IActionResult Get() => new OkObjectResult(updater.Current);

    [Post("/check")]
    [Produces(200, typeof(AppUpdateInfo))]
    public async Task<IActionResult> Check(CancellationToken ct)
        => new OkObjectResult(await updater.Check(ct));

    [Put("/skip")]
    [Produces(200, typeof(AppUpdateInfo))]
    public async Task<IActionResult> Skip(SkipAppUpdateRequest request, CancellationToken ct)
        => new OkObjectResult(await updater.Skip(request.Skip, ct));

    /// <summary>
    /// Answered as soon as the download starts. The app quits once the new copy is verified, and
    /// this response has to leave through the listener before that - the page polls <see cref="Get"/>
    /// for progress and reloads when the new copy is answering.
    /// </summary>
    [Post("/install")]
    [Produces(200, typeof(AppUpdateInfo))]
    [Produces(409)]
    public async Task<IActionResult> Install(HttpContext ctx, CancellationToken ct)
    {
        if (updater.BeginInstall() is not { } started)
            return new ConflictObjectResult(updater.Current.CannotInstallReason ?? "There is no update to install.");

        if (await current.Resolve(ctx, ct) is { } user)
        {
            await activity.Log(
                ActivityAction.Login, user.Id, user.Username, success: true,
                detail: $"Started installing FileServe {started.LatestVersion}",
                context: current.GetActivityContext(ctx), ct: ct
            );
        }

        return new OkObjectResult(started);
    }
}
