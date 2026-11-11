using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Files;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server;

/// <summary>
/// The upload-check-apply mechanics of a restore, shared by the administrator's settings and by
/// first-run setup. Who may restore is the endpoints' business; this only holds the staged file.
/// </summary>
public class BackupStaging(IBackupService backups, IFileServeServer server, FileServeServerOptions options)
{
    /// <summary>Where an upload waits between being checked and being applied.</summary>
    string StagedPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.TicketKeyPath))!, "restore-staged.db");

    /// <summary>
    /// Writes the first file in a multipart body to the staging path and reports what is in it.
    /// Null when the request carried no file at all. An unusable file is deleted rather than left
    /// sitting around looking like it can be restored.
    /// </summary>
    public async Task<BackupInfo?> Stage(HttpContext ctx, CancellationToken ct)
    {
        var staged = this.StagedPath;
        var received = false;

        await foreach (var part in ctx.Request.ReadMultipartAsync(ct))
        {
            if (!part.IsFile)
                continue;

            await using (var file = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await part.Body.CopyToAsync(file, ct);

            received = true;
            break;
        }

        if (!received)
            return null;

        var info = await backups.Inspect(staged, ct);
        if (!info.IsValid)
            File.Delete(staged);

        return info;
    }

    /// <summary>Checks the staged file again; null when nothing has been staged.</summary>
    public async Task<BackupInfo?> InspectStaged(CancellationToken ct)
        => File.Exists(this.StagedPath) ? await backups.Inspect(this.StagedPath, ct) : null;

    /// <summary>
    /// Applies the staged backup after a short delay. The swap stops the listener the caller's
    /// response is travelling over, so the response has to be on its way first.
    /// </summary>
    public void ApplyInBackground()
    {
        var staged = this.StagedPath;

        _ = Task.Run(async () =>
        {
            // Long enough for the 202 to reach the browser before the socket goes away.
            await Task.Delay(400);

            var wasRunning = server.IsRunning;
            await server.Stop();

            try
            {
                await backups.Apply(staged);
                File.Delete(staged);
            }
            finally
            {
                if (wasRunning)
                    await server.Start();
            }
        }, CancellationToken.None);
    }

    public void Discard()
    {
        if (File.Exists(this.StagedPath))
            File.Delete(this.StagedPath);
    }

    public static bool IsMultipart(HttpContext ctx)
        => ctx.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) ?? false;

    public static BackupSummary ToSummary(BackupInfo info) => new(
        info.IsValid, info.Problem, info.Users, info.Administrators,
        info.Shares, info.ActivityEntries, info.SizeBytes, info.TakenUtc
    );
}
