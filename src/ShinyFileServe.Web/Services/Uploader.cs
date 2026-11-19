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

    /// <summary>Uploads whatever is selected in an <c>&lt;input type="file"&gt;</c>, then clears it.</summary>
    public Task<List<FileEntryInfo>> FromInput(
        ElementReference input,
        string folderId,
        string? path,
        IProgress<UploadProgress> progress,
        CancellationToken ct = default)
        => this.Send(input, [], folderId, path, progress, ct);

    /// <summary>Uploads the files of the last drop whose names are in <paramref name="names"/>.</summary>
    public Task<List<FileEntryInfo>> FromDrop(
        IReadOnlyList<string> names,
        string folderId,
        string? path,
        IProgress<UploadProgress> progress,
        CancellationToken ct = default)
        => this.Send("drop", names, folderId, path, progress, ct);

    async Task<List<FileEntryInfo>> Send(
        object source,
        IReadOnlyList<string> names,
        string folderId,
        string? path,
        IProgress<UploadProgress> progress,
        CancellationToken ct)
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
        var result = await module.InvokeAsync<JsonElement>("start", CancellationToken.None, id, source, names, url, callback);
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
