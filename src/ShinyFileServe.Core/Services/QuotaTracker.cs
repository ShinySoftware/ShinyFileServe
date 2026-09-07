using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

public interface IQuotaTracker
{
    Task<QuotaUsage> GetUsage(string userId, string folderId, QuotaWindow window, CancellationToken ct = default);
    Task RecordDownload(string userId, string folderId, QuotaWindow window, long bytes, CancellationToken ct = default);
    Task RecordUpload(string userId, string folderId, QuotaWindow window, long bytes, CancellationToken ct = default);
}

public class QuotaTracker(IDocumentStore store, TimeProvider time) : IQuotaTracker
{
    public async Task<QuotaUsage> GetUsage(string userId, string folderId, QuotaWindow window, CancellationToken ct = default)
    {
        if (window == QuotaWindow.None)
            return new QuotaUsage { Window = QuotaWindow.None, UserId = userId, FolderId = folderId };

        var (key, start, end) = Period(window, time.GetUtcNow());
        var id = QuotaUsage.MakeId(userId, folderId, window, key);

        return await store.Get<QuotaUsage>(id, cancellationToken: ct) ?? new QuotaUsage
        {
            Id = id,
            UserId = userId,
            FolderId = folderId,
            Window = window,
            PeriodKey = key,
            PeriodStartUtc = start,
            PeriodEndUtc = end
        };
    }

    public Task RecordDownload(string userId, string folderId, QuotaWindow window, long bytes, CancellationToken ct = default)
        => this.Record(userId, folderId, window, bytes, download: true, ct);

    public Task RecordUpload(string userId, string folderId, QuotaWindow window, long bytes, CancellationToken ct = default)
        => this.Record(userId, folderId, window, bytes, download: false, ct);

    async Task Record(string userId, string folderId, QuotaWindow window, long bytes, bool download, CancellationToken ct)
    {
        if (window == QuotaWindow.None || bytes <= 0)
            return;

        var usage = await this.GetUsage(userId, folderId, window, ct);
        var isNew = await store.Get<QuotaUsage>(usage.Id, cancellationToken: ct) is null;

        if (download)
            usage.DownloadedBytes += bytes;
        else
            usage.UploadedBytes += bytes;

        if (isNew)
            await store.Insert(usage, cancellationToken: ct);
        else
            await store.Update(usage, cancellationToken: ct);
    }

    /// <summary>
    /// The key identifying the current period, plus its bounds. Weeks are ISO-8601 (Monday start) so a
    /// "weekly" quota does not roll over on a different day depending on the server's culture.
    /// </summary>
    public static (string Key, DateTimeOffset Start, DateTimeOffset? End) Period(QuotaWindow window, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        switch (window)
        {
            case QuotaWindow.Daily:
            {
                var start = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
                return (utc.ToString("yyyy-MM-dd"), start, start.AddDays(1));
            }
            case QuotaWindow.Weekly:
            {
                var dow = (int)utc.DayOfWeek;
                var offset = dow == 0 ? 6 : dow - 1;   // Monday = start of week
                var start = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-offset);
                return ($"{System.Globalization.ISOWeek.GetYear(utc.UtcDateTime):0000}-W{System.Globalization.ISOWeek.GetWeekOfYear(utc.UtcDateTime):00}", start, start.AddDays(7));
            }
            case QuotaWindow.Monthly:
            {
                var start = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
                return (utc.ToString("yyyy-MM"), start, start.AddMonths(1));
            }
            case QuotaWindow.Total:
                return ("all", DateTimeOffset.MinValue, null);

            default:
                return ("none", DateTimeOffset.MinValue, null);
        }
    }
}
