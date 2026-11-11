using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Files;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// Taking a copy of the database and putting one back. One file carries everything - accounts,
/// groups, shares, grants, quota counters, settings and the activity log - so a backup is the whole
/// server and a restore replaces the whole server.
/// </summary>
[Route("/api/admin/backup")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminBackupEndpoints(
    IBackupService backups,
    BackupStaging staging,
    ICurrentUserResolver current,
    IActivityLogger activity
)
{
    [Get]
    [Produces(200)]
    public async Task<IActionResult> Download(HttpContext ctx, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"fileserve-backup-{Guid.NewGuid():n}.db");
        await backups.Create(temp, ct);

        await this.Log(ctx, "Downloaded a backup of the database", ct);

        var name = $"fileserve-{DateTimeOffset.Now:yyyy-MM-dd-HHmm}.db";
        var info = new FileInfo(temp);

        // DeleteOnClose rather than a cleanup afterwards: the response streams the file, so it has
        // to outlive this method, and the handle going away is the only reliable moment to remove it.
        return FileDownloadResult.FromOpener(
            _ => ValueTask.FromResult<Stream>(new FileStream(
                temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous
            )),
            info.Length,
            "application/octet-stream",
            name
        );
    }

    /// <summary>
    /// Accepts a backup and reports what is in it. Deliberately separate from applying it: nobody
    /// should replace every account on the server without being shown what they are replacing them
    /// with first.
    /// </summary>
    [Post("/stage")]
    [Produces(200, typeof(BackupSummary))]
    [Produces(400)]
    public async Task<IActionResult> Stage(HttpContext ctx, CancellationToken ct)
    {
        if (!BackupStaging.IsMultipart(ctx))
            return new BadRequestObjectResult("Expected a multipart/form-data body.");

        if (await staging.Stage(ctx, ct) is not { } info)
            return new BadRequestObjectResult("No file was present in the request.");

        if (!info.IsValid)
            await this.Log(ctx, $"Rejected a backup: {info.Problem}", ct);

        return new OkObjectResult(BackupStaging.ToSummary(info));
    }

    /// <summary>
    /// Applies the staged backup. Answers 202 and does the work afterwards, because the swap stops
    /// the listener this response is travelling over - the same bargain as stopping the server.
    /// </summary>
    [Post("/restore")]
    [Produces(202)]
    [Produces(409)]
    public async Task<IActionResult> Restore(HttpContext ctx, CancellationToken ct)
    {
        if (await staging.InspectStaged(ct) is not { } info)
            return new ConflictObjectResult("Upload a backup first.");

        if (!info.IsValid)
            return new ConflictObjectResult(info.Problem ?? "That backup cannot be restored.");

        await this.Log(ctx, $"Restored the database from a backup ({info.Users} accounts, {info.Shares} shares)", ct);

        staging.ApplyInBackground();
        return new StatusCodeResult(StatusCodes.Status202Accepted);
    }

    /// <summary>Throws away a staged upload without applying it.</summary>
    [Delete("/stage")]
    [Produces(204)]
    public IActionResult Discard()
    {
        staging.Discard();
        return new NoContentResult();
    }

    async Task Log(HttpContext ctx, string detail, CancellationToken ct)
    {
        if (await current.Resolve(ctx, ct) is not { } me)
            return;

        await activity.Log(
            ActivityAction.Login, me.Id, me.Username, success: true,
            detail: detail, context: current.GetActivityContext(ctx), ct: ct
        );
    }
}
