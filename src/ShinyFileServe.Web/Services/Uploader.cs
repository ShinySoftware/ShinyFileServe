using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Web.Services;

/// <summary>How far an upload has got. <see cref="Total"/> is the whole request, multipart framing included.</summary>
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

    /// <summary>Uploads a claim's files to a folder.</summary>
    public async Task<List<FileEntryInfo>> Send(
        string claim,
        string folderId,
        string? path,
        IProgress<UploadProgress> progress,
        CancellationToken ct = default)
    {
        var module = await this.Module();
        var id = Guid.NewGuid().ToString("N");
        var url = $"api/files/upload?{FileServeApi.Query(folderId, path)}&overwrite=true";

        using var callback = DotNetObjectReference.Create(new ProgressCallback(progress));
        await using var cancel = ct.Register(() => _ = module.InvokeVoidAsync("abort", id).AsTask());

        // CancellationToken.None on purpose: a cancel is the abort above, which the script answers
        // with -1. Cancelling the interop call itself would leave the request running.
        //
        // Read as a JsonElement rather than a record: the interop serializer works by reflection,
        // and a trimmed publish would be free to strip a record it could not see being built.
        var result = await module.InvokeAsync<JsonElement>("start", CancellationToken.None, id, claim, url, callback);
        var code = result.GetProperty("status").GetInt32();
        var body = result.GetProperty("body").GetString() ?? "";

        if (code == -1)
            throw new OperationCanceledException(ct);

        if (code == 0)
            throw new ApiException(0, "The connection dropped before the upload finished.");

        var status = (HttpStatusCode)code;
        if (code is < 200 or > 299)
            throw new ApiException(status, FileServeApi.Describe(status, body));

        return JsonSerializer.Deserialize(body.Length == 0 ? "[]" : body, ClientJsonContext.Default.ListFileEntryInfo) ?? [];
    }

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

    class ProgressCallback(IProgress<UploadProgress> progress)
    {
        [JSInvokable]
        public void OnProgress(long loaded, long total) => progress.Report(new UploadProgress(loaded, total));
    }
}
