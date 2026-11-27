using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ShinyFileServe.Tests.Infrastructure;

/// <summary>Test data.</summary>
public static class Data
{
    public static byte[] Random(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public const int MB = 1024 * 1024;
}

/// <summary>
/// A signed-in client with the requests the browser makes: downloads, the multipart upload, and the
/// tus requests <c>upload.js</c> sends for a resumable upload.
/// </summary>
public sealed class TestClient(HttpClient http, FileServeHost host) : IDisposable
{
    public HttpClient Http => http;

    // -- downloads -------------------------------------------------------

    public string DownloadUrl(string path) => $"api/files/download?folderId={host.FolderId}&path={Uri.EscapeDataString(path)}";

    public HttpRequestMessage DownloadRequest(string path) => this.New(HttpMethod.Get, this.DownloadUrl(path));

    public async Task<HttpResponseMessage> Download(string path, Action<HttpRequestMessage>? configure = null, CancellationToken ct = default)
    {
        var request = this.DownloadRequest(path);
        configure?.Invoke(request);
        return this.Checked(await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct));
    }

    /// <summary>
    /// Starts a download and walks away after <paramref name="keep"/> bytes, the way a dropped
    /// connection or a closed laptop ends one. Returns what arrived and the response's validator.
    /// </summary>
    public async Task<(byte[] Received, string ETag)> DownloadAndCutOff(string path, int keep)
    {
        using var cts = new CancellationTokenSource();
        var response = await this.Download(path, ct: cts.Token);
        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag!.ToString();

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[keep];
        var read = 0;
        while (read < keep)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, keep - read), cts.Token);
            if (n == 0)
                throw new InvalidOperationException("The download ended before the cut-off point.");
            read += n;
        }

        // Cancelling and disposing mid-body resets the stream (HTTP/2) or closes the connection (HTTP/1.1).
        await cts.CancelAsync();
        response.Dispose();
        return (buffer, etag);
    }

    // -- multipart upload ------------------------------------------------

    public async Task<HttpResponseMessage> UploadMultipart(string? path, params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            // Browsers never send a length per part; nor does this, so the server cannot lean on one.
            part.Headers.ContentLength = null;
            form.Add(part, "files", name);
        }

        var url = $"api/files/upload?folderId={host.FolderId}&overwrite=true";
        if (path is not null)
            url += $"&path={Uri.EscapeDataString(path)}";

        var request = this.New(HttpMethod.Post, url);
        request.Content = form;
        return this.Checked(await http.SendAsync(request));
    }

    // -- tus -------------------------------------------------------------

    public async Task<string> TusCreate(string name, long length, string? path = null, bool overwrite = true)
    {
        var request = Tus(HttpMethod.Post, "api/uploads");
        request.Headers.Add("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("Upload-Metadata", String.Join(",", new[]
        {
            Meta("folderId", host.FolderId),
            Meta("path", path ?? ""),
            Meta("filename", name),
            Meta("overwrite", overwrite ? "true" : "false")
        }));

        using var response = this.Checked(await http.SendAsync(request));
        if (response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException($"Create answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        // The path alone, as upload.js keeps it.
        return new Uri(http.BaseAddress!, response.Headers.Location!).AbsolutePath.TrimStart('/');
    }

    public async Task<HttpResponseMessage> TusPatch(string url, long offset, ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        var request = Tus(HttpMethod.Patch, url);
        request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
        request.Content = new ReadOnlyMemoryContent(chunk);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        return this.Checked(await http.SendAsync(request, ct));
    }

    /// <summary>A PATCH with a body of the caller's making - one that stalls, say. Waits only for the headers.</summary>
    public async Task<HttpResponseMessage> TusPatch(string url, long offset, HttpContent body, CancellationToken ct = default)
    {
        var request = Tus(HttpMethod.Patch, url);
        request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
        request.Content = body;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        return this.Checked(await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct));
    }

    /// <summary>
    /// A PATCH whose connection dies part way through the body: <paramref name="sendBeforeDrop"/>
    /// bytes go out, then the request is torn down. What the server keeps of it is up to the server.
    /// </summary>
    public async Task TusPatchCutOff(string url, long offset, byte[] chunk, int sendBeforeDrop)
    {
        using var cts = new CancellationTokenSource();
        var request = Tus(HttpMethod.Patch, url);
        request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
        request.Content = new DroppingContent(chunk, sendBeforeDrop, cts);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        request.Content.Headers.ContentLength = chunk.Length;

        try
        {
            using var response = await http.SendAsync(request, cts.Token);
            throw new InvalidOperationException($"The cut-off PATCH should not have completed, but answered {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
        }
    }

    public async Task<HttpResponseMessage> TusHead(string url)
    {
        var request = Tus(HttpMethod.Head, url);
        return this.Checked(await http.SendAsync(request));
    }

    public async Task<long> TusOffset(string url)
    {
        using var response = await this.TusHead(url);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HEAD answered {(int)response.StatusCode}");

        return Int64.Parse(response.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Sends the rest of an upload from <paramref name="offset"/> in chunks of <paramref name="chunkSize"/>,
    /// exactly as upload.js does. Returns the reply to the last chunk.
    /// </summary>
    public async Task<HttpResponseMessage> TusSendFrom(string url, byte[] bytes, long offset, int chunkSize)
    {
        while (true)
        {
            var end = (int)Math.Min(offset + chunkSize, bytes.Length);
            var response = await this.TusPatch(url, offset, bytes.AsMemory((int)offset, end - (int)offset));
            if (response.StatusCode != HttpStatusCode.NoContent)
                return response;

            offset = Int64.Parse(response.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
            if (offset >= bytes.Length)
                return response;

            response.Dispose();
        }
    }

    public async Task<HttpResponseMessage> TusUpload(string name, byte[] bytes, int chunkSize, string? path = null)
    {
        var url = await this.TusCreate(name, bytes.Length, path);
        return await this.TusSendFrom(url, bytes, 0, chunkSize);
    }

    public static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public static string? UnescapedHeader(HttpResponseMessage response, string name)
        => Header(response, name) is { } value ? Uri.UnescapeDataString(value) : null;

    /// <summary>
    /// A request on this client's protocol. HttpClient's default version only applies to the requests
    /// its own helpers build, so without this a hand-built request quietly goes out as HTTP/1.1.
    /// </summary>
    HttpRequestMessage New(HttpMethod method, string url) => new(method, url)
    {
        Version = http.DefaultRequestVersion,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact
    };

    /// <summary>Every response is checked to have come over the protocol the test asked for.</summary>
    HttpResponseMessage Checked(HttpResponseMessage response)
    {
        if (response.Version != http.DefaultRequestVersion)
            throw new InvalidOperationException($"Expected HTTP/{http.DefaultRequestVersion} but got HTTP/{response.Version}");

        return response;
    }

    HttpRequestMessage Tus(HttpMethod method, string url)
    {
        var request = this.New(method, url);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        return request;
    }

    static string Meta(string key, string value) => $"{key} {Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}";

    public void Dispose() => http.Dispose();

    /// <summary>Writes part of a body, flushes it so it reaches the server, then kills the request.</summary>
    sealed class DroppingContent(byte[] bytes, int sendBeforeDrop, CancellationTokenSource kill) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes.AsMemory(0, sendBeforeDrop));
            await stream.FlushAsync();

            // Give the server time to take in what was sent before the connection goes.
            await Task.Delay(500);
            await kill.CancelAsync();
            throw new IOException("Connection dropped (simulated).");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }
}
