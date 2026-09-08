using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

/// <summary>Bytes moved by one account since the last flush. Both directions, because a request has both.</summary>
public record BandwidthDelta(string UserId, string Username, string DisplayName, long BytesIn, long BytesOut, DateTimeOffset AtUtc);

/// <summary>
/// The bandwidth ledger: what each account moved, by the hour, kept so it can be asked about later.
/// </summary>
/// <remarks>
/// Written in batches rather than per request. A busy minute is thousands of responses and one row
/// per hour per account, so batching turns thousands of read-modify-writes into one - which is also
/// what makes it safe to meter every request rather than only the interesting ones.
/// </remarks>
public interface IBandwidthMeter
{
    /// <summary>Adds a batch to the ledger. Deltas landing in the same hour for the same account are merged.</summary>
    Task Record(IReadOnlyCollection<BandwidthDelta> deltas, CancellationToken ct = default);

    Task<BandwidthReport> Query(BandwidthQuery query, CancellationToken ct = default);

    /// <summary>Drops hours older than the retention window. Returns how many rows went.</summary>
    Task<int> Prune(TimeSpan retention, CancellationToken ct = default);
}

public class BandwidthMeter(IDocumentStore store, TimeProvider time) : IBandwidthMeter
{
    /// <summary>
    /// Writes are serialised. Every row here is a read-modify-write of a counter, and two flushes
    /// overlapping on the same hour would silently lose whichever read first - the one thing a
    /// ledger may not do.
    /// </summary>
    readonly SemaphoreSlim writes = new(1, 1);

    public async Task Record(IReadOnlyCollection<BandwidthDelta> deltas, CancellationToken ct = default)
    {
        if (deltas.Count == 0)
            return;

        // Merged before anything is read, so a batch covering a hundred requests from one person
        // touches their row once.
        var merged = new Dictionary<string, BandwidthUsage>();
        foreach (var delta in deltas)
        {
            if (delta.BytesIn <= 0 && delta.BytesOut <= 0)
                continue;

            var hour = BandwidthUsage.Hour(delta.AtUtc);
            var id = BandwidthUsage.MakeId(delta.UserId, hour);

            if (!merged.TryGetValue(id, out var row))
            {
                row = new BandwidthUsage
                {
                    Id = id,
                    UserId = delta.UserId,
                    Username = delta.Username,
                    DisplayName = delta.DisplayName,
                    HourUtc = hour
                };
                merged[id] = row;
            }

            row.BytesIn += Math.Max(0, delta.BytesIn);
            row.BytesOut += Math.Max(0, delta.BytesOut);
        }

        if (merged.Count == 0)
            return;

        await this.writes.WaitAsync(ct);
        try
        {
            foreach (var row in merged.Values)
            {
                var existing = await store.Get<BandwidthUsage>(row.Id, cancellationToken: ct);
                if (existing is null)
                {
                    await store.Insert(row, cancellationToken: ct);
                    continue;
                }

                existing.BytesIn += row.BytesIn;
                existing.BytesOut += row.BytesOut;

                // A rename should show on the traffic it moved after the rename, and the row is the
                // only place the name is kept.
                existing.Username = row.Username;
                existing.DisplayName = row.DisplayName;

                await store.Update(existing, cancellationToken: ct);
            }
        }
        finally
        {
            this.writes.Release();
        }
    }

    public async Task<BandwidthReport> Query(BandwidthQuery query, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();

        // An open-ended window is still a window on the report - the chart has to say what it covers,
        // and "everything" is not an axis.
        var to = (query.ToUtc ?? now).ToUniversalTime();
        var from = (query.FromUtc ?? to.AddDays(-30)).ToUniversalTime();

        if (from > to)
            (from, to) = (to, from);

        var fromHour = BandwidthUsage.Hour(from);

        var rows = store.Query<BandwidthUsage>()
            .Where(x => x.HourUtc >= fromHour)
            .Where(x => x.HourUtc <= to);

        if (!String.IsNullOrWhiteSpace(query.UserId))
            rows = rows.Where(x => x.UserId == query.UserId);

        var found = await rows.ToList(ct);

        var periods = new Dictionary<DateTimeOffset, (long In, long Out)>();
        var users = new Dictionary<string, BandwidthByUser>();
        long totalIn = 0, totalOut = 0;

        foreach (var row in found)
        {
            var start = Bucket(row.HourUtc, query.Bucket);
            var period = periods.GetValueOrDefault(start);
            periods[start] = (period.In + row.BytesIn, period.Out + row.BytesOut);

            var user = users.GetValueOrDefault(row.UserId)
                ?? new BandwidthByUser(row.UserId, row.Username, row.DisplayName, 0, 0);

            users[row.UserId] = user with
            {
                // The newest row wins the name, so a report reads in today's vocabulary.
                Username = row.Username,
                DisplayName = row.DisplayName,
                BytesIn = user.BytesIn + row.BytesIn,
                BytesOut = user.BytesOut + row.BytesOut
            };

            totalIn += row.BytesIn;
            totalOut += row.BytesOut;
        }

        return new BandwidthReport(
            from,
            to,
            query.Bucket,
            totalIn,
            totalOut,
            Fill(from, to, query.Bucket, periods),
            users.Values.OrderByDescending(x => x.Total).ToList()
        );
    }

    public async Task<int> Prune(TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - retention;
        return await store.Query<BandwidthUsage>().Where(x => x.HourUtc < cutoff).ExecuteDelete(ct);
    }

    /// <summary>The period an hour belongs to. Days start at midnight UTC, which is what the rows are keyed on.</summary>
    static DateTimeOffset Bucket(DateTimeOffset hour, BandwidthBucket bucket)
    {
        var utc = hour.ToUniversalTime();
        return bucket == BandwidthBucket.Day
            ? new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero)
            : BandwidthUsage.Hour(utc);
    }

    /// <summary>
    /// Every period in the window, including the empty ones. A chart that only plots the periods
    /// with traffic in them draws a quiet weekend as no time at all.
    /// </summary>
    static List<BandwidthPeriod> Fill(
        DateTimeOffset from,
        DateTimeOffset to,
        BandwidthBucket bucket,
        Dictionary<DateTimeOffset, (long In, long Out)> found)
    {
        // A year by the hour is nine thousand columns nobody can see. The window is honoured; the
        // resolution is the thing that gives, and the caller asked for a shape rather than a promise.
        const int MaxPeriods = 800;

        var step = bucket == BandwidthBucket.Day ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);
        var cursor = Bucket(from, bucket);
        var list = new List<BandwidthPeriod>();

        while (cursor <= to && list.Count < MaxPeriods)
        {
            var value = found.GetValueOrDefault(cursor);
            list.Add(new BandwidthPeriod(cursor, value.In, value.Out));
            cursor += step;
        }

        return list;
    }
}
