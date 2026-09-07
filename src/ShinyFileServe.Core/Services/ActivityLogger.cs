using Shiny.DocumentDb;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Core.Services;

/// <summary>What the caller knows about the request behind an action. All of it is optional.</summary>
public record ActivityContext(string? IpAddress = null, string? UserAgent = null);

public interface IActivityLogger
{
    Task Log(
        ActivityAction action,
        string? userId,
        string username,
        bool success,
        string? folderId = null,
        string? folderName = null,
        string? path = null,
        long bytes = 0,
        string? detail = null,
        ActivityContext? context = null,
        CancellationToken ct = default
    );

    Task<PagedResponse<ActivityEntryInfo>> Query(ActivityQuery query, CancellationToken ct = default);

    /// <summary>Drops entries older than the retention window. Returns how many were removed.</summary>
    Task<int> Prune(TimeSpan retention, CancellationToken ct = default);

    /// <summary>
    /// Raised after an entry is written, on whichever thread wrote it - which is a request thread.
    /// Handlers must not block: this is the seam desktop notifications hang off, and a slow one
    /// would show up as a slow download.
    /// </summary>
    event EventHandler<ActivityLogEntry>? Logged;
}

public class ActivityLogger(IDocumentStore store, TimeProvider time) : IActivityLogger
{
    public event EventHandler<ActivityLogEntry>? Logged;

    public async Task Log(
        ActivityAction action,
        string? userId,
        string username,
        bool success,
        string? folderId = null,
        string? folderName = null,
        string? path = null,
        long bytes = 0,
        string? detail = null,
        ActivityContext? context = null,
        CancellationToken ct = default)
    {
        var entry = new ActivityLogEntry
        {
            Id = Ids.New(),
            TimestampUtc = time.GetUtcNow(),
            UserId = userId,
            Username = username,
            Action = action,
            FolderId = folderId,
            FolderName = folderName,
            Path = path,
            Bytes = bytes,
            Success = success,
            Detail = detail,
            IpAddress = context?.IpAddress,
            UserAgent = context?.UserAgent
        };
        await store.Insert(entry, cancellationToken: ct);
        this.Logged?.Invoke(this, entry);
    }

    public async Task<PagedResponse<ActivityEntryInfo>> Query(ActivityQuery query, CancellationToken ct = default)
    {
        var q = store.Query<ActivityLogEntry>();

        if (!String.IsNullOrWhiteSpace(query.UserId))
            q = q.Where(x => x.UserId == query.UserId);

        if (!String.IsNullOrWhiteSpace(query.FolderId))
            q = q.Where(x => x.FolderId == query.FolderId);

        if (query.Action is { } action)
            q = q.Where(x => x.Action == action);

        if (query.FromUtc is { } from)
            q = q.Where(x => x.TimestampUtc >= from);

        if (query.ToUtc is { } to)
            q = q.Where(x => x.TimestampUtc <= to);

        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var page = Math.Max(1, query.Page);

        var result = await q.OrderByDescending(x => x.TimestampUtc).PageResult(page, pageSize, ct: ct);

        var items = result.Records.Select(ToInfo).ToList();
        return new PagedResponse<ActivityEntryInfo>(items, result.TotalCount, result.Page, result.PageSize);
    }

    public async Task<int> Prune(TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - retention;
        return await store.Query<ActivityLogEntry>().Where(x => x.TimestampUtc < cutoff).ExecuteDelete(ct);
    }

    public static ActivityEntryInfo ToInfo(ActivityLogEntry x) => new(
        x.Id,
        x.TimestampUtc,
        x.UserId,
        x.Username,
        x.Action,
        x.FolderId,
        x.FolderName,
        x.Path,
        x.Bytes,
        x.Success,
        x.Detail,
        x.IpAddress
    );
}
