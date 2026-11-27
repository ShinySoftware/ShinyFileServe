using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using ShinyFileServe.Tests.Infrastructure;

namespace ShinyFileServe.Tests;

/// <summary>
/// The two reports: "my download got cut off and I had to start again" and "uploads just don't
/// complete". Each test here is one way that happens in the field, reproduced against the real
/// server, with a deadline - a hang is the failure, not a slow pass.
/// </summary>
public class ReportedSymptomTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static CancellationTokenSource Deadline(int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    // -- downloads ----------------------------------------------------------

    /// <summary>
    /// The HTTP/2 flow-control race fixed in HttpServer 3.1: a lost window refill left a download
    /// waiting forever part way through, about one large download in four.
    /// </summary>
    [Fact]
    public async Task Http2_large_downloads_never_stall_part_way()
    {
        await using var host = await FileServeHost.Start();
        var bytes = host.PutFile("large.bin", 128 * Data.MB);
        var expected = Data.Hash(bytes);
        using var client = await host.Client(Wire.Https2);

        for (var i = 0; i < 8; i++)
        {
            using var deadline = Deadline(30);
            using var response = await client.Download("large.bin", ct: deadline.Token);
            Assert.Equal(expected, Data.Hash(await response.Content.ReadAsByteArrayAsync(deadline.Token)));
        }
    }

    [Fact]
    public async Task Http2_large_downloads_side_by_side_never_stall()
    {
        await using var host = await FileServeHost.Start();
        var files = Enumerable.Range(0, 4).Select(i => host.PutFile($"l{i}.bin", 64 * Data.MB, seed: i)).ToList();
        using var client = await host.Client(Wire.Https2);

        for (var round = 0; round < 3; round++)
        {
            using var deadline = Deadline(60);
            var results = await Task.WhenAll(files.Select(async (bytes, i) =>
            {
                using var response = await client.Download($"l{i}.bin", ct: deadline.Token);
                return (bytes, got: await response.Content.ReadAsByteArrayAsync(deadline.Token));
            }));
            Assert.All(results, r => Assert.Equal(Data.Hash(r.bytes), Data.Hash(r.got)));
        }
    }

    /// <summary>
    /// The laptop sleeps or the Wi-Fi changes mid-download. The browser's Resume sends a range on a
    /// new connection; with a parallel-download limit, the dead download must not still be holding
    /// the only slot.
    /// </summary>
    [Theory]
    [InlineData(Wire.Http11)]
    [InlineData(Wire.Https2)]
    public async Task A_download_whose_client_vanished_can_be_resumed_from_a_new_connection(Wire wire)
    {
        await using var host = await FileServeHost.Start(s =>
        {
            s.MaxParallelDownloads = 1;
            s.StallTimeout = TimeSpan.FromSeconds(5);
        });
        var bytes = host.PutFile("film.bin", 64 * Data.MB);
        await using var proxy = new VanishingProxy(wire == Wire.Http11 ? host.HttpPort : host.HttpsPort);

        using var lost = await host.Client(wire, proxy);
        var response = await lost.Download("film.bin");
        var etag = response.Headers.ETag!.ToString();
        var stream = await response.Content.ReadAsStreamAsync(Ct);
        var received = new byte[4 * Data.MB];
        var read = 0;
        while (read < received.Length)
            read += await stream.ReadAsync(received.AsMemory(read), Ct);

        proxy.Vanish();

        // Back online, on a new connection, the browser resumes.
        using var back = await host.Client(wire);
        var watch = Stopwatch.StartNew();
        HttpResponseMessage? resumed = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            resumed = await back.Download("film.bin", r =>
            {
                r.Headers.Range = new RangeHeaderValue(read, null);
                r.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag));
            });
            if (resumed.StatusCode != HttpStatusCode.TooManyRequests)
                break;

            resumed.Dispose();
            await Task.Delay(500, Ct);
        }

        using (resumed)
        {
            Assert.True(resumed!.StatusCode == HttpStatusCode.PartialContent,
                $"Resume answered {(int)resumed.StatusCode} after {watch.Elapsed.TotalSeconds:0}s: the vanished download is still holding the parallel slot.");
            var rest = await resumed.Content.ReadAsByteArrayAsync(Ct);
            Assert.Equal(Data.Hash(bytes), Data.Hash([.. received.AsSpan(0, read), .. rest]));
        }
    }

    // -- uploads that are refused must not hang the connection ----------------

    /// <summary>
    /// The HTTP/2 hang fixed in HttpServer 3.1: a handler that answered without reading a large
    /// request body stalled the whole connection forever. FileServe answers without reading in many
    /// places - over the cap, over quota, too many uploads at once, session expired - so on 3.0 a
    /// refused upload took every request on the connection down with it, and the browser's upload
    /// simply never finished.
    /// </summary>
    [Fact]
    public async Task Http2_an_upload_refused_over_the_cap_answers_and_the_connection_carries_on()
    {
        await using var host = await FileServeHost.Start(s => s.MaxUploadFileSizeBytes = Data.MB);
        var small = host.PutFile("small.bin", 1000);
        using var client = await host.Client(Wire.Https2);

        for (var i = 0; i < 3; i++)
        {
            using var deadline = Deadline(20);
            using (var refused = await client.UploadMultipart(null, ("big.bin", Data.Random(20 * Data.MB))).WaitAsync(deadline.Token))
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);

            using var after = await client.Download("small.bin", ct: deadline.Token);
            Assert.Equal(small, await after.Content.ReadAsByteArrayAsync(deadline.Token));
        }
    }

    [Fact]
    public async Task Http2_a_chunk_for_an_upload_that_is_gone_answers_and_the_connection_carries_on()
    {
        await using var host = await FileServeHost.Start();
        var small = host.PutFile("small.bin", 1000);
        using var client = await host.Client(Wire.Https2);

        for (var i = 0; i < 3; i++)
        {
            using var deadline = Deadline(20);
            using (var gone = await client.TusPatch("api/uploads/0123456789abcdef0123456789abcdef", 0, Data.Random(8 * Data.MB), deadline.Token))
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

            using var after = await client.Download("small.bin", ct: deadline.Token);
            Assert.Equal(small, await after.Content.ReadAsByteArrayAsync(deadline.Token));
        }
    }

    [Fact]
    public async Task Http2_a_chunk_sent_after_the_session_ended_answers_401_and_the_upload_resumes_after_signing_in()
    {
        await using var host = await FileServeHost.Start();
        using var client = await host.Client(Wire.Https2);
        var bytes = Data.Random(6 * Data.MB);
        var url = await client.TusCreate("later.bin", bytes.Length);
        using (var first = await client.TusPatch(url, 0, bytes.AsMemory(0, 2 * Data.MB)))
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Signed out (idle timeout, another tab, an expired cookie) with the upload half done.
        using (var signOut = await client.Http.PostAsync("api/auth/logout", null, Ct))
        {
        }

        using (var deadline = Deadline(20))
        using (var refused = await client.TusPatch(url, 2 * Data.MB, bytes.AsMemory(2 * Data.MB, 2 * Data.MB), deadline.Token))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // Signs in again on the same connection and carries on.
        using (var login = await client.Http.PostAsync("api/auth/login",
            System.Net.Http.Json.JsonContent.Create(new { username = FileServeHost.Username, password = FileServeHost.Password }), Ct))
            login.EnsureSuccessStatusCode();

        var offset = await client.TusOffset(url);
        using var last = await client.TusSendFrom(url, bytes, offset, 2 * Data.MB);
        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.Equal(Data.Hash(bytes), Data.Hash(File.ReadAllBytes(host.SharePath("later.bin"))));
    }

    [Fact]
    public async Task Http2_an_upload_refused_for_too_many_at_once_answers_while_the_other_carries_on()
    {
        await using var host = await FileServeHost.Start(s => s.MaxParallelUploads = 1);
        using var client = await host.Client(Wire.Https2);
        var big = Data.Random(40 * Data.MB);

        // One upload running; a second at the same moment is refused without its body being read.
        var running = client.UploadMultipart(null, ("first.bin", big));
        await Task.Delay(200, Ct);

        using (var deadline = Deadline(20))
        {
            using var refused = await client.UploadMultipart(null, ("second.bin", Data.Random(20 * Data.MB))).WaitAsync(deadline.Token);
            Assert.True(refused.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.OK, $"{refused.StatusCode}");
        }

        using (var deadline = Deadline(30))
        using (var done = await running.WaitAsync(deadline.Token))
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        Assert.Equal(Data.Hash(big), Data.Hash(File.ReadAllBytes(host.SharePath("first.bin"))));
    }

    // -- uploads whose client vanished ------------------------------------------

    /// <summary>
    /// The connection goes mid-chunk without a word. The browser comes back on a new connection and
    /// does what upload.js does: ask where things stand (a 423 there means the dead request still has
    /// the upload, so it carries on with its last known offset), PATCH, and on a 409 ask again. The
    /// dead request must let go and the upload must finish - well inside the four and a half minutes
    /// upload.js keeps retrying for.
    /// </summary>
    [Theory]
    [InlineData(Wire.Http11)]
    [InlineData(Wire.Https2)]
    public async Task A_resumable_upload_whose_client_vanished_mid_chunk_finishes_from_a_new_connection(Wire wire)
    {
        await using var host = await FileServeHost.Start(s => s.StallTimeout = TimeSpan.FromSeconds(5));
        var bytes = Data.Random(24 * Data.MB);
        const int chunk = 8 * Data.MB;
        await using var proxy = new VanishingProxy(wire == Wire.Http11 ? host.HttpPort : host.HttpsPort);

        using var lost = await host.Client(wire, proxy);
        var url = await lost.TusCreate("trip.bin", bytes.Length);
        using (var first = await lost.TusPatch(url, 0, bytes.AsMemory(0, chunk)))
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // The second chunk is part way up when the network goes.
        var stuck = lost.TusPatch(url, chunk, new SlowBody(bytes.AsMemory(chunk, chunk), proxy.Vanish));
        await Task.Delay(1500, Ct);

        using var back = await host.Client(wire);
        var watch = Stopwatch.StartNew();
        long offset = chunk;
        var attempts = new List<string>();

        while (offset < bytes.Length && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            using (var head = await back.TusHead(url))
            {
                attempts.Add($"HEAD {(int)head.StatusCode}");
                if (head.IsSuccessStatusCode)
                    offset = Int64.Parse(head.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
            }

            var end = (int)Math.Min(offset + chunk, bytes.Length);
            using var patch = await back.TusPatch(url, offset, bytes.AsMemory((int)offset, end - (int)offset));
            attempts.Add($"PATCH@{offset} {(int)patch.StatusCode}");
            if (patch.StatusCode == HttpStatusCode.NoContent)
                offset = Int64.Parse(patch.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
            else
                await Task.Delay(250, Ct);
        }

        Assert.True(offset >= bytes.Length, $"Did not finish in {watch.Elapsed.TotalSeconds:0}s: {String.Join(", ", attempts)}");
        Assert.Equal(Data.Hash(bytes), Data.Hash(File.ReadAllBytes(host.SharePath("trip.bin"))));
        _ = stuck;
    }

    /// <summary>
    /// An ordinary (multipart) upload whose client vanished part way. With a limit of one upload at a
    /// time, the dead one must give the slot back so the person can upload again once they are back.
    /// </summary>
    [Theory]
    [InlineData(Wire.Http11)]
    [InlineData(Wire.Https2)]
    public async Task An_upload_whose_client_vanished_gives_its_parallel_slot_back(Wire wire)
    {
        await using var host = await FileServeHost.Start(s =>
        {
            s.MaxParallelUploads = 1;
            s.StallTimeout = TimeSpan.FromSeconds(5);
        });
        await using var proxy = new VanishingProxy(wire == Wire.Http11 ? host.HttpPort : host.HttpsPort);

        using var lost = await host.Client(wire, proxy);
        var form = new MultipartFormDataContent { { new SlowBody(Data.Random(12 * Data.MB), proxy.Vanish), "files", "lost.bin" } };
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/files/upload?folderId={host.FolderId}&overwrite=true")
        {
            Version = lost.Http.DefaultRequestVersion,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = form
        };
        _ = lost.Http.SendAsync(request, Ct);
        await Task.Delay(1500, Ct);

        using var back = await host.Client(wire);
        var watch = Stopwatch.StartNew();
        HttpResponseMessage? again = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            again = await back.UploadMultipart(null, ("again.bin", Data.Random(1000)));
            if (again.StatusCode != HttpStatusCode.TooManyRequests)
                break;

            again.Dispose();
            await Task.Delay(500, Ct);
        }

        using (again)
        {
            Assert.True(again!.StatusCode == HttpStatusCode.OK,
                $"Upload answered {(int)again.StatusCode} after {watch.Elapsed.TotalSeconds:0}s: the vanished upload is still holding the parallel slot.");
        }
        Assert.Empty(host.StrayTempFiles());
    }

    /// <summary>A body that sends part of itself, then makes the network vanish and never sends the rest.</summary>
    sealed class SlowBody(ReadOnlyMemory<byte> bytes, Action vanish) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes[..(bytes.Length / 3)]);
            await stream.FlushAsync();
            await Task.Delay(500);
            vanish();
            await Task.Delay(Timeout.Infinite);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }
}
