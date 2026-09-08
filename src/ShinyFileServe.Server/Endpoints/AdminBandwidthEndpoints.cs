using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Server.Transfers;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Endpoints;

/// <summary>
/// How much has gone through the server, over a window, by period and by account. Administrator-only:
/// it names every account and says what each of them moved.
/// </summary>
[Route("/api/admin/bandwidth")]
[Authorize(Roles = ClaimTypesEx.AdminRole)]
public class AdminBandwidthEndpoints(IBandwidthTracker tracker, IBandwidthMeter meter)
{
    /// <summary>
    /// The report. Both ends of the window are optional - with neither, it answers for the last
    /// thirty days, which is the question somebody opening the screen is usually asking.
    /// </summary>
    [Get]
    [Produces(200, typeof(BandwidthReport))]
    [Produces(403)]
    public async Task<IActionResult> Query(
        string? userId = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        BandwidthBucket bucket = BandwidthBucket.Day,
        CancellationToken ct = default)
    {
        // Counts that have not reached the ledger yet would otherwise be missing from a report asked
        // for straight after a transfer, which reads as the report being broken rather than as it
        // being ten seconds behind.
        await tracker.Flush(ct);

        var report = await meter.Query(new BandwidthQuery(userId, fromUtc, toUtc, bucket), ct);
        return new OkObjectResult(report);
    }
}
