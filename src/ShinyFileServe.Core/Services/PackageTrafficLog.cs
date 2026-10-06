using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

/// <summary>
/// Every request the package feeds answer: who, with which token, for which package, and how it went.
/// </summary>
/// <remarks>
/// Buffered rather than written per request. A restore is dozens of requests in a second, and a
/// database write in the path of each would be the slowest part of the feed; a batch every few
/// seconds is one transaction instead. The price is that a report asked for in the same breath as a
/// restore could miss the tail of it, which <see cref="Query"/> settles by flushing first.
/// </remarks>
public interface IPackageTrafficLog
{
    /// <summary>Queues one request. Cheap, and never throws - called from the end of a request.</summary>
    void Record(PackageRequestLogEntry entry);

    /// <summary>Writes whatever is queued. Called by the timer, before a query, and on shutdown.</summary>
    Task Flush(CancellationToken ct = default);

    Task<PagedResponse<PackageRequestInfo>> Query(PackageTrafficQuery query, CancellationToken ct = default);

    /// <summary>Drops entries older than the retention window. Returns how many were removed.</summary>
    Task<int> Prune(TimeSpan retention, CancellationToken ct = default);
}

public class PackageTrafficLog : IPackageTrafficLog, IDisposable
{
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A ceiling on what waits in memory. Past it the oldest are dropped rather than the process
    /// growing without end behind a database that has stopped answering.
    /// </summary>
    const int MaxPending = 50_000;

    readonly ConcurrentQueue<PackageRequestLogEntry> pending = new();
    readonly SemaphoreSlim writes = new(1, 1);
    readonly IDocumentStore store;
    readonly TimeProvider time;
    readonly ILogger<PackageTrafficLog> logger;
    readonly ITimer flusher;

    public PackageTrafficLog(IDocumentStore store, TimeProvider time, ILogger<PackageTrafficLog> logger)
    {
        this.store = store;
        this.time = time;
        this.logger = logger;
        this.flusher = time.CreateTimer(_ => _ = this.FlushQuietly(), null, FlushInterval, FlushInterval);
    }

    public void Record(PackageRequestLogEntry entry)
    {
        if (String.IsNullOrEmpty(entry.Id))
            entry.Id = Ids.New();

        entry.PackageKey = entry.PackageId?.ToLowerInvariant();
        this.pending.Enqueue(entry);

        while (this.pending.Count > MaxPending && this.pending.TryDequeue(out _))
        {
        }
    }

    public async Task Flush(CancellationToken ct = default)
    {
        if (this.pending.IsEmpty)
            return;

        await this.writes.WaitAsync(ct);
        try
        {
            var batch = new List<PackageRequestLogEntry>(this.pending.Count);
            while (this.pending.TryDequeue(out var entry))
                batch.Add(entry);

            if (batch.Count > 0)
                await this.store.BatchInsert(batch, CoreJsonContext.Default.PackageRequestLogEntry, ct);
        }
        finally
        {
            this.writes.Release();
        }
    }

    async Task FlushQuietly()
    {
        try
        {
            await this.Flush();
        }
        catch (Exception ex)
        {
            // A batch that failed is lost rather than retried: the same rows would fail the same way
            // on every tick, and a log that stalls the feed is worse than a log with a gap in it.
            this.logger.LogWarning(ex, "Could not write the package traffic log");
        }
    }

    public async Task<PagedResponse<PackageRequestInfo>> Query(PackageTrafficQuery query, CancellationToken ct = default)
    {
        await this.Flush(ct);

        var q = this.store.Query<PackageRequestLogEntry>();

        if (!String.IsNullOrWhiteSpace(query.UserId))
            q = q.Where(x => x.UserId == query.UserId);

        if (!String.IsNullOrWhiteSpace(query.TokenId))
            q = q.Where(x => x.TokenId == query.TokenId);

        if (query.Feed is { } feed)
            q = q.Where(x => x.Feed == feed);

        if (query.Operation is { } operation)
            q = q.Where(x => x.Operation == operation);

        if (!String.IsNullOrWhiteSpace(query.PackageId))
        {
            var key = query.PackageId.Trim().ToLowerInvariant();
            q = q.Where(x => x.PackageKey == key);
        }

        if (query.RefusedOnly)
            q = q.Where(x => x.StatusCode == 401 || x.StatusCode == 403);

        if (query.FromUtc is { } from)
            q = q.Where(x => x.TimestampUtc >= from);

        if (query.ToUtc is { } to)
            q = q.Where(x => x.TimestampUtc <= to);

        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var page = Math.Max(1, query.Page);

        var result = await q.OrderByDescending(x => x.TimestampUtc).PageResult(page, pageSize, ct: ct);
        var items = result.Records.Select(ToInfo).ToList();
        return new PagedResponse<PackageRequestInfo>(items, result.TotalCount, result.Page, result.PageSize);
    }

    public async Task<int> Prune(TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = this.time.GetUtcNow() - retention;
        return await this.store.Query<PackageRequestLogEntry>().Where(x => x.TimestampUtc < cutoff).ExecuteDelete(ct);
    }

    static PackageRequestInfo ToInfo(PackageRequestLogEntry x) => new(
        x.Id,
        x.TimestampUtc,
        x.Feed,
        x.Operation,
        x.Method,
        x.Path,
        x.PackageId,
        x.Version,
        x.StatusCode,
        x.BytesIn,
        x.BytesOut,
        x.DurationMs,
        x.UserId,
        x.Username,
        x.TokenId,
        x.TokenName,
        x.IpAddress,
        x.UserAgent
    );

    public void Dispose()
    {
        this.flusher.Dispose();

        // What is still queued at shutdown is the end of somebody's restore - worth the wait.
        try
        {
            this.Flush().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Could not write the package traffic log on shutdown");
        }
    }
}
