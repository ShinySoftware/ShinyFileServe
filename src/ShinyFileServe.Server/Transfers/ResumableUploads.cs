using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Shiny.DocumentDb;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Tus;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Shared;
using CoreUser = ShinyFileServe.Core.Documents.User;

namespace ShinyFileServe.Server.Transfers;

/// <summary>
/// Resumable uploads over tus. The protocol is the tus package's; what this adds is everything the
/// multipart upload already does - who may write where, the per-file cap, the quotas, the parallel
/// limit, the virus scan, the activity log and the live transfer view - spread across the moments
/// tus has instead of one request: the create, each chunk, and the last byte.
/// <para>
/// The quotas are checked against the declared size when the upload is created, and that size is
/// held against them until the upload finishes, is cancelled, or expires - so several unfinished
/// uploads cannot each fit on their own and together overrun what is left.
/// </para>
/// </summary>
public sealed class ResumableUploads : IDisposable
{
    /// <summary>Where the tus endpoint is mounted. Uploads live at <c>{Prefix}/{id}</c>.</summary>
    public const string Prefix = "/api/uploads";

    /// <summary>A chunk request that has gone quiet for this long is a client that left; its live transfer is closed off.</summary>
    static readonly TimeSpan IdleTransfer = TimeSpan.FromSeconds(60);

    readonly IDocumentStore documents;
    readonly ICurrentUserResolver current;
    readonly IAccessResolver access;
    readonly IFileService files;
    readonly IQuotaTracker quotas;
    readonly ITransferMonitor transfers;
    readonly IUploadScanner scanner;
    readonly IActivityLogger activity;
    readonly TimeProvider time;
    readonly ILogger<ResumableUploads> logger;
    readonly ReservingStore store;
    readonly DiskTusStore disk;
    readonly ConcurrentDictionary<string, LiveUpload> live = new();
    readonly ITimer idleSweep;

    // Creates are quick and rare, so they take turns: the quota check and the reservation it is
    // checked against have to be one step, or two uploads can both fit the same last gigabyte.
    readonly SemaphoreSlim creating = new(1, 1);

    public ResumableUploads(
        FileServeServerOptions options,
        IDocumentStore documents,
        ICurrentUserResolver current,
        IAccessResolver access,
        IFileService files,
        IQuotaTracker quotas,
        ITransferMonitor transfers,
        IUploadScanner scanner,
        IActivityLogger activity,
        TimeProvider time,
        ILogger<ResumableUploads> logger)
    {
        this.documents = documents;
        this.current = current;
        this.access = access;
        this.files = files;
        this.quotas = quotas;
        this.transfers = transfers;
        this.scanner = scanner;
        this.activity = activity;
        this.time = time;
        this.logger = logger;

        Directory.CreateDirectory(options.UploadsDirectory);
        this.disk = new DiskTusStore(options.UploadsDirectory);
        this.store = new ReservingStore(this.disk, documents);

        this.Options = new TusOptions
        {
            Store = this.store,
            Expiration = options.ResumableUploadExpiry,

            // The size has to be known up front - it is what the quotas are checked against - and
            // a file is one upload, not parts joined on the server.
            AllowDeferredLength = false,
            AllowConcatenation = false,
            AllowTermination = true,

            OnBeforeCreateAsync = this.BeforeCreate,
            OnUploadCompleteAsync = this.Complete
        };

        this.idleSweep = time.CreateTimer(_ => this.SweepIdle(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public TusOptions Options { get; }

    // -----------------------------------------------------------------------
    // The gate: runs ahead of every request to the endpoint
    // -----------------------------------------------------------------------

    /// <summary>
    /// Ahead of the tus handler. A create is serialised and handed somewhere to leave its
    /// reservation; every request to an existing upload must come from the account that started it,
    /// which still has to be allowed to upload there; and a chunk is counted for the live view.
    /// </summary>
    internal async ValueTask Gate(HttpContext context, RequestDelegate next)
    {
        var ct = context.RequestAborted;
        var user = await current.Resolve(context, ct);
        if (user is null)
        {
            await Refuse(context, StatusCodes.Status401Unauthorized, "Your session has expired. Sign in again.");
            return;
        }

        var id = IdFrom(context.Request.Path);
        var method = EffectiveMethod(context.Request);

        if (id.Length == 0)
        {
            if (method != "POST")
            {
                await next(context);
                return;
            }

            await this.creating.WaitAsync(ct);
            try
            {
                using var scope = ReservingStore.BeginCreate();
                await next(context);
            }
            finally
            {
                this.creating.Release();
            }
            return;
        }

        if (method == "OPTIONS")
        {
            await next(context);
            return;
        }

        // Somebody else's upload answers exactly like one that does not exist.
        var upload = await documents.Get<ResumableUpload>(id, cancellationToken: ct);
        if (upload is null || upload.UserId != user.Id)
        {
            await Refuse(context, StatusCodes.Status404NotFound, "That upload does not exist, or has expired.");
            return;
        }

        if (method == "DELETE")
        {
            await next(context);
            this.Close(id, transfer => transfer.Fail("Cancelled"));
            return;
        }

        if (method != "PATCH")
        {
            await next(context);
            return;
        }

        // Asked on every chunk, not just at the start: an administrator who takes the right away
        // part way through means it, and the bytes already here go with it.
        var folder = await access.GetAccess(user, upload.FolderId, ct);
        if (folder is null || !folder.Permissions.Has(FolderPermissions.CreateFile))
        {
            await activity.Log(
                ActivityAction.Denied, user.Id, user.Username, false, upload.FolderId, folder?.Folder.Name, upload.Path,
                upload.Length, "Upload permission withdrawn part way through a resumable upload",
                current.GetActivityContext(context), ct
            );
            await this.store.DeleteAsync(id, ct);
            this.Close(id, transfer => transfer.Fail("Permission withdrawn"));
            await Refuse(context, StatusCodes.Status403Forbidden, "You no longer have permission to upload to this folder.");
            return;
        }

        var entry = this.Track(upload, user, folder, OffsetOf(context.Request));
        Interlocked.Increment(ref entry.InFlight);
        try
        {
            context.Request.Body = new CountingStream(context.Request.Body, entry.Transfer, leaveInnerOpen: true, ownsHandle: false);
            await next(context);
        }
        finally
        {
            entry.LastActivity = time.GetUtcNow();
            Interlocked.Decrement(ref entry.InFlight);
        }
    }

    // -----------------------------------------------------------------------
    // Create
    // -----------------------------------------------------------------------

    async ValueTask BeforeCreate(TusCreateContext ctx)
    {
        var http = ctx.HttpContext;
        var ct = http.RequestAborted;
        var activityContext = current.GetActivityContext(http);

        var user = await current.Resolve(http, ct);
        if (user is null)
        {
            ctx.Reject(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in again.");
            return;
        }

        if (ctx.UploadLength is not { } length)
        {
            ctx.Reject(StatusCodes.Status400BadRequest, "The file's size has to be sent when the upload starts.");
            return;
        }

        var folderId = ctx.Metadata.GetValueOrDefault("folderId");
        var name = SafeName(ctx.Metadata.GetValueOrDefault("filename"));
        var path = Normalize(ctx.Metadata.GetValueOrDefault("path"));
        var overwrite = ctx.Metadata.GetValueOrDefault("overwrite") == "true";
        if (String.IsNullOrEmpty(folderId) || name is null)
        {
            ctx.Reject(StatusCodes.Status400BadRequest, "An upload needs a folder and a file name.");
            return;
        }

        var target = Combine(path, name);
        var folder = await access.GetAccess(user, folderId, ct);
        if (folder is null)
        {
            await activity.Log(
                ActivityAction.Denied, user.Id, user.Username, false, folderId, null, target,
                detail: "No access to folder", context: activityContext, ct: ct
            );
            ctx.Reject(StatusCodes.Status404NotFound, "That folder is no longer there.");
            return;
        }

        if (!folder.Permissions.Has(FolderPermissions.CreateFile))
        {
            await activity.Log(
                ActivityAction.Denied, user.Id, user.Username, false, folderId, folder.Folder.Name, target,
                detail: $"Missing permission: {FolderPermissions.CreateFile.ToDisplay()}", context: activityContext, ct: ct
            );
            ctx.Reject(StatusCodes.Status403Forbidden, "You do not have permission to upload to this folder.");
            return;
        }

        var refusal = await this.Refusal(user, folder, path, target, length, overwrite, ct);
        if (refusal is not null)
        {
            await activity.Log(
                ActivityAction.Denied, user.Id, user.Username, false, folderId, folder.Folder.Name, target,
                length, refusal.Value.Message, activityContext, ct
            );
            ctx.Reject(refusal.Value.Status, refusal.Value.Message);
            return;
        }

        ReservingStore.Reserve(new ResumableUpload
        {
            UserId = user.Id,
            FolderId = folderId,
            Path = target,
            Length = length,
            Overwrite = overwrite,
            CreatedUtc = time.GetUtcNow()
        });
    }

    /// <summary>Everything that would turn the upload away before its first byte, or null when nothing does.</summary>
    async Task<(int Status, string Message)?> Refusal(
        CoreUser user, FolderAccess folder, string path, string target, long length, bool overwrite, CancellationToken ct)
    {
        try
        {
            files.ResolvePath(folder, target);
        }
        catch (FileAccessException ex)
        {
            return (StatusCodes.Status400BadRequest, ex.Message);
        }

        var parent = files.ResolvePath(folder, path);
        if (!Directory.Exists(parent))
            return (StatusCodes.Status404NotFound, "The folder this was going into is no longer there.");

        if (folder.MaxUploadFileSizeBytes is { } cap && length > cap)
            return (StatusCodes.Status413PayloadTooLarge, $"This folder accepts files up to {Bytes(cap)}.");

        var full = files.ResolvePath(folder, target);
        if (!overwrite && File.Exists(full))
            return (StatusCodes.Status409Conflict, "A file with that name already exists.");

        if (user.MaxParallelUploads is { } maxParallel && transfers.CountLive(user.Id, TransferDirection.Upload) >= maxParallel)
        {
            return (
                StatusCodes.Status429TooManyRequests,
                $"You already have {maxParallel} {(maxParallel == 1 ? "upload" : "uploads")} running. Wait for one to finish and try again."
            );
        }

        var reserved = await this.Reservations(user.Id, ct);

        if (folder.UploadWindow != QuotaWindow.None && folder.UploadLimit is { } limit)
        {
            var usage = await quotas.GetUsage(user.Id, folder.Folder.Id, folder.UploadWindow, ct);
            var held = reserved.Where(r => r.FolderId == folder.Folder.Id).Sum(r => r.Length);
            var remaining = Math.Max(0, limit - usage.UploadedBytes - held);
            if (length > remaining)
                return (StatusCodes.Status429TooManyRequests, $"That is {Bytes(length)}, and you have {Bytes(remaining)} of upload quota left in this folder.");
        }

        if (user.UploadQuotaWindow != QuotaWindow.None && user.UploadQuotaBytes is { } accountLimit)
        {
            var usage = await quotas.GetAccountUsage(user.Id, user.UploadQuotaWindow, ct);
            var remaining = Math.Max(0, accountLimit - usage.UploadedBytes - reserved.Sum(r => r.Length));
            if (length > remaining)
                return (StatusCodes.Status429TooManyRequests, $"That is {Bytes(length)}, and your account has {Bytes(remaining)} of upload quota left.");
        }

        return null;
    }

    /// <summary>
    /// This account's unfinished uploads. A record whose bytes are gone - the server was stopped
    /// while the sweep would have removed them, say - holds nothing back and is tidied away here.
    /// </summary>
    async Task<List<ResumableUpload>> Reservations(string userId, CancellationToken ct)
    {
        var all = await documents.Query<ResumableUpload>().Where(x => x.UserId == userId).ToList(ct);
        var held = new List<ResumableUpload>(all.Count);
        foreach (var reservation in all)
        {
            var upload = await this.store.Inner.GetAsync(reservation.Id, ct);
            if (upload is null)
                await documents.Remove<ResumableUpload>(reservation.Id, cancellationToken: ct);
            else
                held.Add(reservation);
        }
        return held;
    }

    // -----------------------------------------------------------------------
    // Complete
    // -----------------------------------------------------------------------

    /// <summary>
    /// The last byte is in. The file is scanned and moved into its share exactly as a multipart
    /// upload is, inside the request that delivered it, so the client hears how it went. Not
    /// cancelled if that client leaves: the upload is whole, and finishing it is the server's job.
    /// </summary>
    async ValueTask Complete(TusCompleteContext ctx)
    {
        var http = ctx.HttpContext;
        var id = ctx.Upload.Id;
        var ct = CancellationToken.None;
        var activityContext = current.GetActivityContext(http);

        var upload = await documents.Get<ResumableUpload>(id, cancellationToken: ct);
        var user = await current.Resolve(http, ct);
        if (upload is null || user is null)
        {
            await this.Discard(id, "The upload's record was lost");
            throw new TusException(410, "The upload could not be finished. Start it again.");
        }

        var folder = await access.GetAccess(user, upload.FolderId, ct);
        if (folder is null || !folder.Permissions.Has(FolderPermissions.CreateFile))
        {
            await this.Discard(id, "Permission withdrawn");
            throw new TusException(StatusCodes.Status403Forbidden, "You no longer have permission to upload to this folder.");
        }

        SavedFile saved;
        try
        {
            var name = Path.GetFileName(upload.Path);

            // The finished upload is moved into the share, not copied: on the same volume that is a
            // rename, so a 10 GB file is published as fast as a 10 KB one instead of being written
            // to disk a second time while the client waits on its last PATCH. Every way out of here
            // deletes the upload anyway - the store's delete is fine with the data file being gone.
            saved = await files.SaveFile(
                folder, upload.Path, this.disk.GetFilePath(id), UploadRules.Conflict(folder, upload.Overwrite),
                inspect: async (temp, token) =>
                {
                    var decision = await scanner.Scan(temp, name, token);
                    if (!decision.Allowed)
                        throw new FileRejectedException(decision, new FileInfo(temp).Length);
                },
                ct: ct
            );
        }
        catch (FileRejectedException ex)
        {
            await activity.Log(
                ActivityAction.Denied, user.Id, user.Username, false, folder.Folder.Id, folder.Folder.Name,
                upload.Path, ex.Bytes, ex.Message, activityContext, ct
            );
            await this.Discard(id, ex.Message);

            // As the multipart upload answers: 422 is the file being refused, 503 the server unable to tell.
            throw new TusException(
                ex.Decision.Verdict == ScanVerdict.Infected
                    ? StatusCodes.Status422UnprocessableEntity
                    : StatusCodes.Status503ServiceUnavailable,
                ex.Message
            );
        }
        catch (FileAccessException ex)
        {
            await activity.Log(
                ActivityAction.Upload, user.Id, user.Username, false, folder.Folder.Id, folder.Folder.Name,
                upload.Path, upload.Length, ex.Message, activityContext, ct
            );
            await this.Discard(id, ex.Message);
            throw new TusException(StatusCodes.Status409Conflict, ex.Message);
        }

        await quotas.RecordUpload(user.Id, folder.Folder.Id, folder.UploadWindow, saved.Bytes, ct);
        await quotas.RecordAccountUpload(user.Id, user.UploadQuotaWindow, saved.Bytes, ct);
        await activity.Log(
            ActivityAction.Upload, user.Id, user.Username, true, folder.Folder.Id, folder.Folder.Name,
            saved.VirtualPath, saved.Bytes, UploadRules.RenamedDetail(upload.Path, saved), activityContext, ct
        );

        // Where it landed, so the client can say when that is not where it was aimed.
        http.Response.Headers.Set("Upload-Saved-As", Uri.EscapeDataString(saved.VirtualPath));

        this.Close(id, transfer => transfer.Complete());
        await this.store.DeleteAsync(id, ct);
    }

    /// <summary>Throws an upload away, bytes and reservation both, and closes its live transfer as failed.</summary>
    async Task Discard(string id, string reason)
    {
        this.Close(id, transfer => transfer.Fail(reason));
        try
        {
            await this.store.DeleteAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove resumable upload {Id}", id);
        }
    }

    // -----------------------------------------------------------------------
    // Administration: what is sitting half-arrived, and throwing it away
    // -----------------------------------------------------------------------

    /// <summary>
    /// Every upload with bytes on disk that has not finished, newest touch first. Bytes with no
    /// record beside them - left by a crash between the two, say - are listed too, as orphans: they
    /// hold no quota back, but they do take up space until the expiry sweep reaches them.
    /// </summary>
    public async Task<List<PartialUploadInfo>> ListUnfinished(CancellationToken ct)
    {
        var records = (await documents.Query<ResumableUpload>().ToList(ct)).ToDictionary(x => x.Id);
        var ids = Directory.Exists(this.disk.Directory)
            ? Directory
                .EnumerateFiles(this.disk.Directory, "*.info")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Union(records.Keys)
                .ToList()
            : records.Keys.ToList();

        var users = new Dictionary<string, CoreUser?>();
        var folders = new Dictionary<string, ShareFolder?>();
        var list = new List<PartialUploadInfo>(ids.Count);

        foreach (var id in ids)
        {
            var upload = await this.disk.GetAsync(id, ct);
            records.TryGetValue(id, out var record);

            if (upload is null)
            {
                // A record whose bytes are gone holds quota back for nothing - the same tidy-up
                // the create path does when it counts reservations.
                if (record is not null)
                    await documents.Remove<ResumableUpload>(id, cancellationToken: ct);
            }
            else if (!upload.IsComplete)
            {
                var user = record is null ? null : await Lookup(users, record.UserId, k => documents.Get<CoreUser>(k, cancellationToken: ct));
                var folder = record is null ? null : await Lookup(folders, record.FolderId, k => documents.Get<ShareFolder>(k, cancellationToken: ct));
                this.live.TryGetValue(id, out var moving);

                list.Add(new PartialUploadInfo(
                    id,
                    record?.UserId,
                    user?.Username,
                    user is null ? null : (String.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName),
                    record?.FolderId,
                    folder?.Name,
                    record?.Path,
                    upload.Offset,
                    upload.Length ?? record?.Length,
                    record?.CreatedUtc,
                    this.LastTouched(id, record),
                    upload.ExpiresUtc,
                    moving is not null && Volatile.Read(ref moving.InFlight) > 0,
                    record is null
                ));
            }
        }

        return list.OrderByDescending(x => x.LastTouchedUtc).ToList();
    }

    /// <summary>
    /// Throws an unfinished upload away for an administrator: its bytes, its quota reservation and
    /// its live transfer. The client finds out on its next chunk, which answers 404.
    /// Returns false when there was nothing there to remove.
    /// </summary>
    public async Task<bool> Remove(string id, CancellationToken ct)
    {
        var record = await documents.Get<ResumableUpload>(id, cancellationToken: ct);
        var upload = await this.disk.GetAsync(id, ct);
        if (record is null && upload is null)
            return false;

        this.Close(id, transfer => transfer.Fail("Removed by an administrator"));
        await this.store.DeleteAsync(id, ct);
        return true;
    }

    /// <summary>When a chunk last landed: the bytes file is written on every one, the info file on create and expiry changes.</summary>
    DateTimeOffset LastTouched(string id, ResumableUpload? record)
    {
        var data = new FileInfo(this.disk.GetFilePath(id));
        var info = new FileInfo(this.disk.GetFilePath(id) + ".info");
        var times = new[] { data, info }
            .Where(f => f.Exists)
            .Select(f => new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero))
            .ToList();

        return times.Count > 0 ? times.Max() : record?.CreatedUtc ?? DateTimeOffset.MinValue;
    }

    static async Task<T?> Lookup<T>(Dictionary<string, T?> cache, string key, Func<string, Task<T?>> load) where T : class
    {
        if (!cache.TryGetValue(key, out var value))
        {
            value = await load(key);
            cache[key] = value;
        }
        return value;
    }

    // -----------------------------------------------------------------------
    // Live transfers
    // -----------------------------------------------------------------------

    /// <summary>
    /// One live transfer per upload, kept across its chunks rather than one per request, so the live
    /// view shows a file arriving instead of a stream of short transfers. It is closed when the
    /// upload ends, or once it has gone quiet - a client that walked away should not hold a parallel
    /// slot for a day.
    /// </summary>
    sealed class LiveUpload(ITransferHandle transfer, DateTimeOffset now)
    {
        public ITransferHandle Transfer { get; } = transfer;
        public DateTimeOffset LastActivity { get; set; } = now;
        public int InFlight;
    }

    LiveUpload Track(ResumableUpload upload, CoreUser user, FolderAccess folder, long offset)
        => this.live.GetOrAdd(upload.Id, _ => new LiveUpload(
            transfers.Begin(
                TransferDirection.Upload, user.Id, user.Username, user.DisplayName,
                folder.Folder.Id, folder.Folder.Name, upload.Path, Math.Max(0, upload.Length - offset)
            ),
            time.GetUtcNow()
        ));

    void Close(string id, Action<ITransferHandle> outcome)
    {
        if (this.live.TryRemove(id, out var entry))
        {
            outcome(entry.Transfer);
            entry.Transfer.Dispose();
        }
    }

    void SweepIdle()
    {
        var cutoff = time.GetUtcNow() - IdleTransfer;
        var idle = this.live
            .Where(x => Volatile.Read(ref x.Value.InFlight) == 0 && x.Value.LastActivity < cutoff)
            .Select(x => x.Key)
            .ToList();

        // Disposed without an outcome, which the monitor records as abandoned - which it was. A
        // resume later starts a fresh transfer for whatever is left.
        foreach (var id in idle)
            this.Close(id, _ => { });
    }

    public void Dispose()
    {
        this.idleSweep.Dispose();
        foreach (var id in this.live.Keys.ToList())
            this.Close(id, _ => { });
        this.creating.Dispose();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    static async Task Refuse(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.Set("Tus-Resumable", "1.0.0");
        await context.Response.WriteTextAsync(message, "text/plain; charset=utf-8", context.RequestAborted);
    }

    /// <summary>The upload id in <c>{Prefix}/{id}</c>, or "" for the endpoint itself.</summary>
    static string IdFrom(string path)
        => path.Length > Prefix.Length ? path[(Prefix.Length + 1)..].Trim('/') : "";

    /// <summary>tus lets a client tunnel PATCH, DELETE and HEAD through POST; the gate judges the verb it meant.</summary>
    static string EffectiveMethod(HttpRequest request)
    {
        var method = request.Method.ToUpperInvariant();
        var tunnelled = request.Headers.GetFirst("X-HTTP-Method-Override")?.Trim().ToUpperInvariant();
        return method == "POST" && !String.IsNullOrEmpty(tunnelled) ? tunnelled : method;
    }

    static long OffsetOf(HttpRequest request)
        => Int64.TryParse(request.Headers.GetFirst("Upload-Offset"), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : 0;

    /// <summary>The last segment of what the client called the file, or null when that is no usable name.</summary>
    static string? SafeName(string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
            return null;

        var name = value.Replace('\\', '/').Split('/').Last().Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        return name;
    }

    static string Normalize(string? path)
        => String.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Trim('/');

    static string Combine(string parent, string name)
        => parent.Length == 0 ? name : parent + "/" + name;

    static string Bytes(long value)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value} bytes" : $"{size:0.#} {units[unit]}";
    }
}

/// <summary>
/// The disk store with the reservations kept beside it, so the two cannot drift: an upload is
/// recorded in the same step that creates it, and its record goes in the same step that removes it -
/// whether that is the client cancelling, the expiry sweep, or the file moving into its share.
/// </summary>
sealed class ReservingStore(ITusStore inner, IDocumentStore documents) : ITusStore
{
    // Filled by the create hook and read by CreateAsync, which the tus handler calls next on the
    // same request. The box is put in place by the gate, ahead of both, because a value set inside
    // the hook would not flow back out to the handler that called it.
    static readonly AsyncLocal<CreateScope?> Pending = new();

    public ITusStore Inner => inner;

    public static CreateScope BeginCreate()
    {
        var scope = new CreateScope();
        Pending.Value = scope;
        return scope;
    }

    public static void Reserve(ResumableUpload upload)
    {
        var scope = Pending.Value ?? throw new InvalidOperationException("A resumable upload was created outside the gate.");
        scope.Upload = upload;
    }

    public sealed class CreateScope : IDisposable
    {
        public ResumableUpload? Upload { get; set; }
        public void Dispose() => Pending.Value = null;
    }

    public async ValueTask<string> CreateAsync(TusCreateRequest request, CancellationToken cancellationToken)
    {
        // Nothing gets bytes on this server without a record of whose they are and what they hold back.
        var reservation = Pending.Value?.Upload ?? throw new TusException(StatusCodes.Status403Forbidden, "Uploads have to be started through the app.");

        var id = await inner.CreateAsync(request, cancellationToken);
        reservation.Id = id;
        try
        {
            await documents.Insert(reservation, cancellationToken: cancellationToken);
        }
        catch
        {
            await inner.DeleteAsync(id, CancellationToken.None);
            throw;
        }
        return id;
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var removed = await inner.DeleteAsync(id, cancellationToken);
        await documents.Remove<ResumableUpload>(id, cancellationToken: CancellationToken.None);
        return removed;
    }

    public ValueTask<TusUpload?> GetAsync(string id, CancellationToken cancellationToken)
        => inner.GetAsync(id, cancellationToken);

    public ValueTask<long> AppendAsync(string id, long offset, Stream data, CancellationToken cancellationToken)
        => inner.AppendAsync(id, offset, data, cancellationToken);

    public ValueTask SetLengthAsync(string id, long length, CancellationToken cancellationToken)
        => inner.SetLengthAsync(id, length, cancellationToken);

    public ValueTask SetExpirationAsync(string id, DateTimeOffset? expiresUtc, CancellationToken cancellationToken)
        => inner.SetExpirationAsync(id, expiresUtc, cancellationToken);

    public ValueTask ConcatenateAsync(string id, IReadOnlyList<string> partialIds, CancellationToken cancellationToken)
        => inner.ConcatenateAsync(id, partialIds, cancellationToken);

    public ValueTask<Stream> OpenReadAsync(string id, CancellationToken cancellationToken)
        => inner.OpenReadAsync(id, cancellationToken);

    public ValueTask<IReadOnlyList<string>> GetExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => inner.GetExpiredAsync(nowUtc, cancellationToken);
}

/// <summary>Puts <see cref="ResumableUploads.Gate"/> in the pipeline for the tus endpoint's paths, and only those.</summary>
public sealed class ResumableUploadGateMiddleware(ResumableUploads uploads) : IHttpMiddleware
{
    public ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
        => IsUnder(context.Request.Path)
            ? uploads.Gate(context, next)
            : next(context);

    static bool IsUnder(string path)
        => path.StartsWith(ResumableUploads.Prefix, StringComparison.OrdinalIgnoreCase)
            && (path.Length == ResumableUploads.Prefix.Length || path[ResumableUploads.Prefix.Length] == '/');
}
