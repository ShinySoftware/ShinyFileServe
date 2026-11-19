using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// How far an upload has got. <see cref="Total"/> is the whole request, multipart framing included,
/// or for a resumable upload the file itself.
/// </summary>
public readonly record struct UploadProgress(long Sent, long Total)
{
    public double Fraction => this.Total > 0 ? Math.Clamp((double)this.Sent / this.Total, 0, 1) : 0;
}

/// <summary>
/// Uploads that report progress. The browser's own XMLHttpRequest does the sending - see
/// <c>upload.js</c> for why HttpClient cannot - straight from the picked or dropped files, so nothing
/// is read into .NET memory and there is no size the browser has to be asked to allow.
/// </summary>
public class Uploader(IJSRuntime js) : IAsyncDisposable
{
    IJSObjectReference? module;

    /// <summary>Starts remembering dropped files. Call before the first drop it should handle.</summary>
    public async Task WatchDrops() => await (await this.Module()).InvokeVoidAsync("watchDrops");

    /// <summary>
    /// Sets aside whatever is selected in an <c>&lt;input type="file"&gt;</c>, then clears it. The
    /// returned claim is what <see cref="Send"/> uploads, however long it waits for its turn.
    /// </summary>
    public async Task<string> ClaimInput(ElementReference input)
        => await (await this.Module()).InvokeAsync<string>("claim", input, Array.Empty<string>());

    /// <summary>Sets aside the files of the last drop whose names are in <paramref name="names"/>.</summary>
    public async Task<string> ClaimDrop(IReadOnlyList<string> names)
        => await (await this.Module()).InvokeAsync<string>("claim", "drop", names);

    /// <summary>Lets go of a claim that was never sent. Harmless on one that was.</summary>
    public async Task Release(string claim)
    {
        try { await (await this.Module()).InvokeVoidAsync("release", claim); }
        catch (JSDisconnectedException) { }
    }

    /// <summary>
    /// Files from this size up go over the resumable upload, one at a time; anything smaller rides
    /// in one ordinary request with the rest. A dropped connection costs a small file almost nothing
    /// to send again, and a resumable upload is two or three requests per file - a folder of photos
    /// would pay for that many times over.
    /// </summary>
    public const long ResumableThreshold = 16L * 1024 * 1024;

    /// <summary>
    /// How much each resumable request carries. Small enough to pass the request-size caps tunnels
    /// and proxies put in front of a server, large enough that the gap between requests is noise.
    /// </summary>
    const long ChunkSize = 16L * 1024 * 1024;

    /// <summary>Uploads some of a claim's files - <paramref name="indexes"/> into it - to a folder, in one request.</summary>
    public async Task<List<FileEntryInfo>> Send(
        string claim,
        IReadOnlyList<int> indexes,
        string folderId,
        string? path,
        IProgress<UploadProgress> progress,
        CancellationToken ct = default)
    {
        var module = await this.Module();
        var id = Guid.NewGuid().ToString("N");
        var url = $"api/files/upload?{FileServeApi.Query(folderId, path)}&overwrite=true";

        using var callback = DotNetObjectReference.Create(new ProgressCallback(progress, null, null));
        await using var cancel = ct.Register(() => _ = module.InvokeVoidAsync("abort", id).AsTask());

        // CancellationToken.None on purpose: a cancel is the abort above, which the script answers
        // with -1. Cancelling the interop call itself would leave the request running.
        //
        // Read as a JsonElement rather than a record: the interop serializer works by reflection,
        // and a trimmed publish would be free to strip a record it could not see being built.
        var result = await module.InvokeAsync<JsonElement>("start", CancellationToken.None, id, claim, indexes, url, callback);
        var body = Check(result, ct);

        return JsonSerializer.Deserialize(body.Length == 0 ? "[]" : body, ClientJsonContext.Default.ListFileEntryInfo) ?? [];
    }

    /// <summary>
    /// Uploads one of a claim's files resumably. Picks up where the server left off if this browser
    /// has sent part of the same file to the same place before. <paramref name="onStatus"/> hears
    /// "Reconnecting…" while it waits out a dropped connection, and null once it is moving again;
    /// <paramref name="onResumed"/> hears how many bytes were already on the server.
    /// </summary>
    public async Task<FileEntryInfo> SendResumable(
        string claim,
        int index,
        string name,
        long size,
        string folderId,
        string? path,
        IProgress<UploadProgress> progress,
        Action<string?> onStatus,
        Action<long> onResumed,
        CancellationToken ct = default)
    {
        var module = await this.Module();
        var id = Guid.NewGuid().ToString("N");
        var metadata = new Dictionary<string, string>
        {
            ["folderId"] = folderId,
            ["path"] = path ?? "",
            ["filename"] = name,
            ["overwrite"] = "true"
        };

        using var callback = DotNetObjectReference.Create(new ProgressCallback(progress, onStatus, onResumed));
        await using var cancel = ct.Register(() => _ = module.InvokeVoidAsync("abort", id).AsTask());

        var result = await module.InvokeAsync<JsonElement>(
            "startResumable", CancellationToken.None, id, claim, index, "api/uploads", metadata, ChunkSize, callback
        );
        Check(result, ct);

        var savedAs = result.TryGetProperty("savedAs", out var saved) && saved.ValueKind == JsonValueKind.String
            ? saved.GetString()!
            : Join(path, name);

        return new FileEntryInfo(savedAs.Split('/').Last(), savedAs, false, size, DateTimeOffset.UtcNow);
    }

    /// <summary>The body of a finished request, or the exception its status calls for.</summary>
    static string Check(JsonElement result, CancellationToken ct)
    {
        var code = result.GetProperty("status").GetInt32();
        var body = result.GetProperty("body").GetString() ?? "";

        if (code == -1)
            throw new OperationCanceledException(ct);

        if (code == 0)
            throw new ApiException(0, body.Length > 0 ? body : "The connection dropped before the upload finished.");

        var status = (HttpStatusCode)code;
        if (code is < 200 or > 299)
            throw new ApiException(status, FileServeApi.Describe(status, body));

        return body;
    }

    static string Join(string? parent, string name)
        => String.IsNullOrEmpty(parent) ? name : $"{parent.Trim('/')}/{name}";

    async Task<IJSObjectReference> Module()
        => this.module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/upload.js");

    public async ValueTask DisposeAsync()
    {
        if (this.module is not null)
        {
            try { await this.module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }

    class ProgressCallback(IProgress<UploadProgress> progress, Action<string?>? status, Action<long>? resumed)
    {
        [JSInvokable]
        public void OnProgress(long loaded, long total) => progress.Report(new UploadProgress(loaded, total));

        [JSInvokable]
        public void OnStatus(string? text) => status?.Invoke(text);

        [JSInvokable]
        public void OnResumed(long offset) => resumed?.Invoke(offset);
    }
}
