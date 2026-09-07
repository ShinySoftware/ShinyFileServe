using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

/// <summary>
/// The per-account caps on the number of downloads and uploads. Byte quotas live on the folder grant
/// and are tracked by <see cref="IQuotaTracker"/>; these are about how often somebody may move a
/// file at all, so they sit on the account and are counted here.
/// </summary>
public interface ITransferCounter
{
    /// <summary>
    /// The counts for the current period. A period nobody has touched reads as zero rather than null,
    /// so a caller never has to distinguish "no rows" from "nothing yet".
    /// </summary>
    Task<TransferCount> GetCount(string userId, QuotaWindow window, CancellationToken ct = default);

    /// <summary>
    /// Adds one to the count for the current period. It reads the row again rather than taking back
    /// the one <see cref="GetCount"/> returned: an upload is recorded when it finishes, and a row
    /// read before the copy started would be minutes stale by then, so two uploads running at once
    /// would both write the same number and only one of them would ever be counted.
    /// </summary>
    Task Record(string userId, QuotaWindow window, bool download, CancellationToken ct = default);
}

public class TransferCounter(IDocumentStore store, TimeProvider time) : ITransferCounter
{
    public async Task<TransferCount> GetCount(string userId, QuotaWindow window, CancellationToken ct = default)
    {
        if (window == QuotaWindow.None)
            return new TransferCount { UserId = userId, Window = QuotaWindow.None };

        // The same period arithmetic the byte quotas use, ISO-8601 weeks and all, so a "weekly" cap
        // and a "weekly" quota roll over together rather than a day apart.
        var (key, start, end) = QuotaTracker.Period(window, time.GetUtcNow());
        var id = TransferCount.MakeId(userId, window, key);

        return await store.Get<TransferCount>(id, cancellationToken: ct) ?? new TransferCount
        {
            Id = id,
            UserId = userId,
            Window = window,
            PeriodKey = key,
            PeriodStartUtc = start,
            PeriodEndUtc = end
        };
    }

    public async Task Record(string userId, QuotaWindow window, bool download, CancellationToken ct = default)
    {
        if (window == QuotaWindow.None)
            return;

        var count = await this.GetCount(userId, window, ct);

        if (download)
            count.Downloads++;
        else
            count.Uploads++;

        // Upsert rather than a read to decide between insert and update: the row for a brand new
        // period does not exist yet, and asking whether it does would be a third read.
        await store.Upsert(count, cancellationToken: ct);
    }
}
