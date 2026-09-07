using Shiny.Net.HttpServer;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Server.Transfers;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// What is happening on the server this second: who is signed in, what is moving, and what has just
/// stopped moving. Administrator-only, because every row names an account and a path inside a share.
/// </summary>
[Route("/api/admin/live")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminLiveEndpoints(ISessionTracker sessions, ITransferMonitor transfers)
{
    /// <summary>
    /// One snapshot of everything the live screen draws. The browser re-asks about once a second
    /// while the screen is open and stops when it closes.
    /// </summary>
    /// <remarks>
    /// Polled rather than streamed. The server can push - <c>ctx.SendEventsAsync</c> is right there -
    /// but a WebAssembly client cannot consume server-sent events without an EventSource bridged
    /// through JS interop, and the whole payload is a few hundred bytes. A second of latency on a
    /// screen a person watches for a minute is not worth a JavaScript file to maintain.
    /// </remarks>
    [Get]
    [Produces(200, typeof(LiveActivitySnapshot))]
    [Produces(403)]
    public IActionResult Get()
    {
        var moving = transfers.Snapshot();

        var here = sessions.All
            .Select(s => new LiveSessionInfo(
                s.UserId,
                s.Username,
                String.IsNullOrWhiteSpace(s.DisplayName) ? s.Username : s.DisplayName,
                s.Address,
                s.SignedInUtc,
                s.LastSeenUtc,
                s.IsLocal
            ))
            .ToList();

        return new OkObjectResult(new LiveActivitySnapshot(here, moving.Live, moving.Finished));
    }
}
