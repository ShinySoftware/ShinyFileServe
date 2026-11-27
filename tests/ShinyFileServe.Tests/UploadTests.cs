using System.Net;
using ShinyFileServe.Server.Transfers;
using ShinyFileServe.Tests.Infrastructure;

namespace ShinyFileServe.Tests;

/// <summary>
/// The ordinary upload: one multipart request carrying one or more files, which is how the browser
/// sends everything under the resumable threshold.
/// </summary>
public class MultipartUploadTests
{
    public static TheoryData<Wire> Wires => [Wire.Http11, Wire.Https11, Wire.Https2];

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task A_batch_of_files_all_land_intact(Wire wire)
    {
        await using var host = await FileServeHost.Start();
        using var client = await host.Client(wire);
        var files = Enumerable.Range(0, 6).Select(i => ($"photo{i}.jpg", Data.Random(300_000 + i * 1000, i))).ToArray();

        using var response = await client.UploadMultipart(null, files);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(files, f => Assert.Equal(Data.Hash(f.Item2), Data.Hash(File.ReadAllBytes(host.SharePath(f.Item1)))));
        Assert.Empty(host.StrayTempFiles());
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task A_large_single_file_lands_intact(Wire wire)
    {
        await using var host = await FileServeHost.Start();
        using var client = await host.Client(wire);
        var bytes = Data.Random(40 * Data.MB);

        using var response = await client.UploadMultipart(null, ("big.bin", bytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Data.Hash(bytes), Data.Hash(File.ReadAllBytes(host.SharePath("big.bin"))));
    }

    [Fact]
    public async Task Several_uploads_at_once_over_one_http2_connection_all_land()
    {
        await using var host = await FileServeHost.Start();
        using var client = await host.Client(Wire.Https2);
        var files = Enumerable.Range(0, 6).Select(i => ($"p{i}.bin", Data.Random(5 * Data.MB + i, i))).ToArray();

        var responses = await Task.WhenAll(files.Select(f => client.UploadMultipart(null, f)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All(files, f => Assert.Equal(Data.Hash(f.Item2), Data.Hash(File.ReadAllBytes(host.SharePath(f.Item1)))));
        foreach (var response in responses)
            response.Dispose();
    }

    [Fact]
    public async Task A_batch_whose_total_passes_the_per_file_cap_is_accepted_when_every_file_is_under_it()
    {
        // Ten files of 3 MB under a 10 MB per-file cap: a 30 MB request. The cap is per file.
        await using var host = await FileServeHost.Start(s => s.MaxUploadFileSizeBytes = 10 * Data.MB);
        using var client = await host.Client(Wire.Https2);
        var files = Enumerable.Range(0, 10).Select(i => ($"img{i}.jpg", Data.Random(3 * Data.MB, i))).ToArray();

        using var response = await client.UploadMultipart(null, files);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(files, f => Assert.True(File.Exists(host.SharePath(f.Item1)), f.Item1));
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task A_file_over_the_per_file_cap_is_refused_and_nothing_is_left_behind(Wire wire)
    {
        await using var host = await FileServeHost.Start(s => s.MaxUploadFileSizeBytes = 2 * Data.MB);
        using var client = await host.Client(wire);

        HttpResponseMessage? response = null;
        try
        {
            response = await client.UploadMultipart(null, ("huge.bin", Data.Random(12 * Data.MB)));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Refusing at the limit can mean answering before the client has finished sending. Over
            // HTTP/1.1 that can surface as a reset instead of the 413; either way it was refused.
        }
        finally
        {
            response?.Dispose();
        }

        Assert.False(File.Exists(host.SharePath("huge.bin")));
        Assert.Empty(host.StrayTempFiles());
        Assert.Equal(0, await host.UploadedBytesToday());
    }

    [Fact]
    public async Task A_file_over_the_quota_left_is_refused_and_not_charged()
    {
        await using var host = await FileServeHost.Start(s => s.UploadQuotaBytes = 4 * Data.MB);
        using var client = await host.Client(Wire.Https2);

        using var fits = await client.UploadMultipart(null, ("a.bin", Data.Random(3 * Data.MB)));
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);

        using var over = await client.UploadMultipart(null, ("b.bin", Data.Random(3 * Data.MB)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.False(File.Exists(host.SharePath("b.bin")));
        Assert.Empty(host.StrayTempFiles());
        Assert.Equal(3 * Data.MB, await host.UploadedBytesToday());
    }

    [Fact]
    public async Task Uploading_into_a_subfolder_lands_there()
    {
        await using var host = await FileServeHost.Start();
        Directory.CreateDirectory(host.SharePath("albums/2026"));
        using var client = await host.Client(Wire.Https2);
        var bytes = Data.Random(100_000);

        using var response = await client.UploadMultipart("albums/2026", ("x.jpg", bytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, File.ReadAllBytes(host.SharePath("albums/2026/x.jpg")));
    }
}

/// <summary>
/// The resumable upload (tus), which the browser uses for every file from 16 MB: each chunk is its
/// own PATCH, a dropped connection costs only what was in flight, and the client asks the server how
/// far it got before carrying on. These drive the same requests <c>upload.js</c> sends.
/// </summary>
public class ResumableUploadTests
{
    public static TheoryData<Wire> Wires => [Wire.Http11, Wire.Https11, Wire.Https2];

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task A_chunked_upload_lands_intact_and_cleans_up_after_itself(Wire wire)
    {
        await using var host = await FileServeHost.Start();
        using var client = await host.Client(wire);
        var bytes = Data.Random(5 * Data.MB + 333);

        using var last = await client.TusUpload("video.mp4", bytes, chunkSize: Data.MB);

        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.Equal("video.mp4", TestClient.UnescapedHeader(last, ResumableUploads.SavedAsHeader));
        Assert.Equal(Data.Hash(bytes), Data.Hash(File.ReadAllBytes(host.SharePath("video.mp4"))));
        Assert.Empty(host.PendingUploadFiles());
        Assert.Empty(host.StrayTempFiles());
        Assert.Equal(0, await host.ReservationCount());
    }

    [Fact]
    public async Task A_large_upload_in_browser_sized_chunks_lands_intact_over_http2()
    {
        await using var host = await FileServeHost.Start();
        using var client = await host.Client(Wire.Https2);
        var bytes = Data.Random(48 * Data.MB + 5);

        using var last = await client.TusUpload("big.bin", bytes, chunkSize: 16 * Data.MB);

        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.Equal(Data.Hash(bytes), Data.Hash(File.ReadAllBytes(host.SharePath("big.bin"))));
    }

    [Fact]
    public async Task Several_resumable_uploads_at_once_over_one_http2_connection_all_land()
    {
        await using var host = await FileServeHost.Start();
        using var client = await host.Client(Wire.Https2);
        var files = Enumerable.Range(0, 4).Select(i => (Name: $"r{i}.bin", Bytes: Data.Random(6 * Data.MB + i, i))).ToArray();

        var replies = await Task.WhenAll(files.Select(f => client.TusUpload(f.Name, f.Bytes, chunkSize: 2 * Data.MB)));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.All(files, f => Assert.Equal(Data.Hash(f.Bytes), Data.Hash(File.ReadAllBytes(host.SharePath(f.Name)))));
        foreach (var reply in replies)
            reply.Dispose();
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task A_chunk_cut_off_mid_way_resumes_from_what_the_server_kept(Wire wire)
    {
        await using var host = await FileServeHost.Start();
        var bytes = Data.Random(6 * Data.MB + 99);
        const int chunk = 2 * Data.MB;

        using var client = await host.Client(wire);
        var url = await client.TusCreate("resume.bin", bytes.Length);
        using (var first = await client.TusPatch(url, 0, bytes.AsMemory(0, chunk)))
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // The second chunk's connection dies after 700 KB.
        await client.TusPatchCutOff(url, chunk, bytes[chunk..(2 * chunk)], 700 * 1024);

        // A fresh client - the old connection is gone - asks where things stand, as upload.js does.
        using var again = await host.Client(wire);
        var offset = await WaitForOffset(again, url, atLeast: chunk);
        Assert.InRange(offset, chunk, 2 * chunk);

        using var last = await again.TusSendFrom(url, bytes, offset, chunk);

        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.Equal(Data.Hash(bytes), Data.Hash(File.ReadAllBytes(host.SharePath("resume.bin"))));
        Assert.Empty(host.PendingUploadFiles());
        Assert.Equal(0, await host.ReservationCount());
    }

    [Fact]
    public async Task A_patch_at_a_stale_offset_is_refused_with_409_and_the_upload_carries_on()
    {
        await using var host = await FileServeHost.Start();
        var bytes = Data.Random(3 * Data.MB);
        using var client = await host.Client(Wire.Https2);

        var url = await client.TusCreate("stale.bin", bytes.Length);
        using (var first = await client.TusPatch(url, 0, bytes.AsMemory(0, Data.MB)))
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // The client believes the first chunk was lost and sends it again.
        using (var stale = await client.TusPatch(url, 0, bytes.AsMemory(0, Data.MB)))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var offset = await client.TusOffset(url);
        Assert.Equal(Data.MB, offset);

        using var last = await client.TusSendFrom(url, bytes, offset, Data.MB);
        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.Equal(Data.Hash(bytes), Data.Hash(File.ReadAllBytes(host.SharePath("stale.bin"))));
    }

    // -- the reply to the last chunk was lost --------------------------------

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task After_the_reply_to_the_last_chunk_is_lost_asking_again_says_it_was_saved(Wire wire)
    {
        await using var host = await FileServeHost.Start();
        var bytes = Data.Random(2 * Data.MB);
        using var client = await host.Client(wire);

        var url = await client.TusCreate("done.bin", bytes.Length);
        using (var last = await client.TusSendFrom(url, bytes, 0, Data.MB))
            Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);

        // The client never saw that 204. It asks how far the upload got...
        using var head = await client.TusHead(url);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(bytes.Length.ToString(), TestClient.Header(head, "Upload-Offset"));
        Assert.Equal("done.bin", TestClient.UnescapedHeader(head, ResumableUploads.SavedAsHeader));
        Assert.Null(TestClient.Header(head, ResumableUploads.ErrorHeader));

        // ...or simply sends the last chunk again, and gets the same answer rather than a 404.
        using var resent = await client.TusPatch(url, bytes.Length - Data.MB, bytes.AsMemory(bytes.Length - Data.MB));
        Assert.Equal(HttpStatusCode.NoContent, resent.StatusCode);
        Assert.Equal("done.bin", TestClient.UnescapedHeader(resent, ResumableUploads.SavedAsHeader));

        Assert.Equal(Data.Hash(bytes), Data.Hash(File.ReadAllBytes(host.SharePath("done.bin"))));
    }

    [Fact]
    public async Task Somebody_else_cannot_read_the_outcome_of_my_finished_upload()
    {
        await using var host = await FileServeHost.Start();
        var users = host.Get<ShinyFileServe.Core.Services.IUserManager>();
        await users.CreateUser("other", FileServeHost.Password, "Other");

        using var client = await host.Client(Wire.Https2);
        var bytes = Data.Random(Data.MB);
        var url = await client.TusCreate("mine.bin", bytes.Length);
        using (var last = await client.TusSendFrom(url, bytes, 0, Data.MB))
            Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);

        using var otherHttp = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
        })
        { BaseAddress = client.Http.BaseAddress };
        using var login = await otherHttp.PostAsync("api/auth/login", System.Net.Http.Json.JsonContent.Create(new { username = "other", password = FileServeHost.Password }), Ct);
        login.EnsureSuccessStatusCode();

        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        head.Headers.Add("Tus-Resumable", "1.0.0");
        using var answer = await otherHttp.SendAsync(head, Ct);
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    // -- a save that fails -----------------------------------------------------

    [Fact]
    public async Task When_the_finished_upload_cannot_be_saved_the_client_is_told_why_and_nothing_is_left_holding_quota()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a read-only directory to make the save fail.");

        await using var host = await FileServeHost.Start(s => s.UploadQuotaBytes = 50 * Data.MB);
        var target = host.SharePath("locked");
        Directory.CreateDirectory(target);
        var bytes = Data.Random(3 * Data.MB);
        using var client = await host.Client(Wire.Https2);

        var url = await client.TusCreate("file.bin", bytes.Length, path: "locked");
        using (var first = await client.TusPatch(url, 0, bytes.AsMemory(0, 2 * Data.MB)))
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // The folder stops being writable before the last chunk lands.
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var last = await client.TusPatch(url, 2 * Data.MB, bytes.AsMemory(2 * Data.MB));
            Assert.Equal(HttpStatusCode.InternalServerError, last.StatusCode);

            // The client retries a 500 by asking where things stand. It must learn the real reason,
            // not "the upload expired".
            using var head = await client.TusHead(url);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Null(TestClient.Header(head, ResumableUploads.SavedAsHeader));
            Assert.Contains("could not save", TestClient.UnescapedHeader(head, ResumableUploads.ErrorHeader));
            Assert.Equal("500", TestClient.Header(head, ResumableUploads.ErrorStatusHeader));
        }
        finally
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Empty(Directory.GetFiles(target));
        Assert.Empty(host.PendingUploadFiles());
        Assert.Equal(0, await host.ReservationCount());
        Assert.Equal(0, await host.UploadedBytesToday());
    }

    // -- limits ------------------------------------------------------------------

    [Fact]
    public async Task An_upload_over_the_per_file_cap_is_refused_at_create()
    {
        await using var host = await FileServeHost.Start(s => s.MaxUploadFileSizeBytes = 2 * Data.MB);
        using var client = await host.Client(Wire.Https2);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.TusCreate("big.bin", 3 * Data.MB));
        Assert.Contains("413", error.Message);
        Assert.Equal(0, await host.ReservationCount());
    }

    [Fact]
    public async Task Unfinished_uploads_hold_their_size_against_the_quota_until_they_finish()
    {
        await using var host = await FileServeHost.Start(s => s.UploadQuotaBytes = 5 * Data.MB);
        using var client = await host.Client(Wire.Https2);

        var first = await client.TusCreate("a.bin", 3 * Data.MB);

        // 3 MB is reserved; 3 MB more would overrun the 5 MB quota even though nothing has arrived yet.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.TusCreate("b.bin", 3 * Data.MB));
        Assert.Contains("429", refused.Message);

        var bytes = Data.Random(3 * Data.MB);
        using (var last = await client.TusSendFrom(first, bytes, 0, Data.MB))
            Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);

        Assert.Equal(3 * Data.MB, await host.UploadedBytesToday());
        Assert.Equal(0, await host.ReservationCount());
    }

    /// <summary>The server may still be writing out the tail of a cut-off chunk when the client asks.</summary>
    static async Task<long> WaitForOffset(TestClient client, string url, long atLeast)
    {
        long offset = 0;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var head = await client.TusHead(url);
            if (head.IsSuccessStatusCode)
            {
                offset = Int64.Parse(head.Headers.GetValues("Upload-Offset").Single());
                if (offset >= atLeast)
                    return offset;
            }

            await Task.Delay(100, Ct);
        }

        return offset;
    }
}
