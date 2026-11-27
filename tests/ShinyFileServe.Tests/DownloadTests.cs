using System.Net;
using System.Net.Http.Headers;
using ShinyFileServe.Tests.Infrastructure;

namespace ShinyFileServe.Tests;

/// <summary>
/// Downloads arrive whole, survive being cut off, and resume from where they stopped - over plain
/// HTTP/1.1, HTTPS HTTP/1.1, and HTTPS HTTP/2, which is what a browser uses against the server's
/// HTTPS port and where the 3.0 download stall lived.
/// </summary>
public class DownloadTests
{
    public static TheoryData<Wire> Wires => [Wire.Http11, Wire.Https11, Wire.Https2];

    public static TheoryData<Wire, int> WiresAndSizes
    {
        get
        {
            var data = new TheoryData<Wire, int>();
            foreach (var wire in Enum.GetValues<Wire>())
            {
                data.Add(wire, 0);
                data.Add(wire, 1);
                data.Add(wire, 5 * Data.MB + 7);
                data.Add(wire, 48 * Data.MB);
            }
            return data;
        }
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(WiresAndSizes))]
    public async Task Whole_file_arrives_intact(Wire wire, int size)
    {
        await using var host = await FileServeHost.Start();
        using var client = await host.Client(wire);
        var bytes = host.PutFile("file.bin", size);

        using var response = await client.Download("file.bin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(size, response.Content.Headers.ContentLength);
        Assert.Equal(Data.Hash(bytes), Data.Hash(await response.Content.ReadAsByteArrayAsync(Ct)));
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task Cut_off_download_resumes_with_a_range_and_the_pieces_make_the_file(Wire wire)
    {
        await using var host = await FileServeHost.Start();
        var bytes = host.PutFile("movie.bin", 24 * Data.MB + 123);

        using var first = await host.Client(wire);
        var (received, etag) = await first.DownloadAndCutOff("movie.bin", 9 * Data.MB);

        // What a browser sends to resume: the rest, provided the file is still the one it started.
        using var second = await host.Client(wire);
        using var resumed = await second.Download("movie.bin", r =>
        {
            r.Headers.Range = new RangeHeaderValue(received.Length, null);
            r.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag));
        });

        Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
        Assert.Equal($"bytes {received.Length}-{bytes.Length - 1}/{bytes.Length}", resumed.Content.Headers.ContentRange!.ToString());

        var rest = await resumed.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(Data.Hash(bytes), Data.Hash([.. received, .. rest]));
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task Resume_against_a_file_that_changed_gets_the_whole_new_file(Wire wire)
    {
        await using var host = await FileServeHost.Start();
        host.PutFile("doc.bin", 4 * Data.MB, seed: 1);

        using var client = await host.Client(wire);
        var (received, etag) = await client.DownloadAndCutOff("doc.bin", Data.MB);

        // Replaced while the download was stopped; the stale piece must not be stitched onto new bytes.
        var replaced = host.PutFile("doc.bin", 4 * Data.MB + 10, seed: 2);
        File.SetLastWriteTimeUtc(host.SharePath("doc.bin"), DateTime.UtcNow.AddMinutes(1));

        using var resumed = await client.Download("doc.bin", r =>
        {
            r.Headers.Range = new RangeHeaderValue(received.Length, null);
            r.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag));
        });

        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(Data.Hash(replaced), Data.Hash(await resumed.Content.ReadAsByteArrayAsync(Ct)));
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task A_file_fetched_in_ranges_reassembles_exactly(Wire wire)
    {
        await using var host = await FileServeHost.Start();
        var bytes = host.PutFile("pieces.bin", 3 * Data.MB + 17);

        using var client = await host.Client(wire);
        var assembled = new List<byte>();
        const int piece = 700 * 1024;
        for (var from = 0; from < bytes.Length; from += piece)
        {
            var to = Math.Min(from + piece, bytes.Length) - 1;
            using var response = await client.Download("pieces.bin", r => r.Headers.Range = new RangeHeaderValue(from, to));
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            assembled.AddRange(await response.Content.ReadAsByteArrayAsync(Ct));
        }

        Assert.Equal(Data.Hash(bytes), Data.Hash(assembled.ToArray()));
    }

    // -- the HTTP/2 stall ---------------------------------------------------

    [Fact]
    public async Task Http2_a_download_nobody_is_reading_does_not_stall_the_others_on_the_connection()
    {
        await using var host = await FileServeHost.Start();
        var big = host.PutFile("big.bin", 32 * Data.MB, seed: 1);
        var small = host.PutFile("small.bin", 8 * Data.MB, seed: 2);
        using var client = await host.Client(Wire.Https2);

        // Headers in, body left unread: its flow-control window fills and it stops. That must hold
        // up this stream only - not the connection.
        using var parked = await client.Download("big.bin");
        Assert.Equal(HttpVersion.Version20, parked.Version);
        await Task.Delay(1500, Ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var other = await client.Download("small.bin", ct: timeout.Token);
        Assert.Equal(Data.Hash(small), Data.Hash(await other.Content.ReadAsByteArrayAsync(timeout.Token)));

        // And the parked one still finishes once it is read.
        Assert.Equal(Data.Hash(big), Data.Hash(await parked.Content.ReadAsByteArrayAsync(Ct)));
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task Many_downloads_at_once_all_complete(Wire wire)
    {
        await using var host = await FileServeHost.Start();
        var files = Enumerable.Range(0, 8).Select(i => (Name: $"f{i}.bin", Bytes: host.PutFile($"f{i}.bin", 6 * Data.MB + i, seed: i))).ToList();
        using var client = await host.Client(wire);

        var results = await Task.WhenAll(files.Select(async f =>
        {
            using var response = await client.Download(f.Name);
            response.EnsureSuccessStatusCode();
            return (f.Bytes, Got: await response.Content.ReadAsByteArrayAsync(Ct));
        }));

        Assert.All(results, r => Assert.Equal(Data.Hash(r.Bytes), Data.Hash(r.Got)));
    }

    [Fact]
    public async Task Http2_a_download_slowed_by_its_reader_still_completes()
    {
        await using var host = await FileServeHost.Start();
        var bytes = host.PutFile("slow.bin", 6 * Data.MB);
        using var client = await host.Client(Wire.Https2);

        using var response = await client.Download("slow.bin");
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var copy = new MemoryStream();
        var buffer = new byte[256 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, Ct)) > 0)
        {
            copy.Write(buffer, 0, n);
            await Task.Delay(15, Ct);
        }

        Assert.Equal(Data.Hash(bytes), Data.Hash(copy.ToArray()));
    }

    // -- quotas and limits --------------------------------------------------

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task Resuming_a_download_is_charged_only_for_the_rest(Wire wire)
    {
        const int size = 10 * Data.MB;

        // Room for one and a half copies. Charging the whole file again for the resume would refuse it.
        await using var host = await FileServeHost.Start(s => s.DownloadQuotaBytes = size + size / 2);
        host.PutFile("q.bin", size);
        using var client = await host.Client(wire);

        var (received, etag) = await client.DownloadAndCutOff("q.bin", size / 2);
        Assert.Equal(size, await host.DownloadedBytesToday());

        using var resumed = await client.Download("q.bin", r =>
        {
            r.Headers.Range = new RangeHeaderValue(received.Length, null);
            r.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag));
        });

        Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
        await resumed.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(size + size / 2, await host.DownloadedBytesToday());
    }

    [Fact]
    public async Task A_range_past_the_quota_is_refused_and_one_that_fits_is_not()
    {
        await using var host = await FileServeHost.Start(s => s.DownloadQuotaBytes = 3 * Data.MB);
        host.PutFile("big.bin", 10 * Data.MB);
        using var client = await host.Client(Wire.Https2);

        using var whole = await client.Download("big.bin");
        Assert.Equal(HttpStatusCode.TooManyRequests, whole.StatusCode);

        using var piece = await client.Download("big.bin", r => r.Headers.Range = new RangeHeaderValue(0, 2 * Data.MB - 1));
        Assert.Equal(HttpStatusCode.PartialContent, piece.StatusCode);
        await piece.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(2 * Data.MB, await host.DownloadedBytesToday());
    }

    [Fact]
    public async Task Not_modified_costs_nothing()
    {
        await using var host = await FileServeHost.Start(s => s.DownloadQuotaBytes = 100 * Data.MB);
        host.PutFile("cached.bin", 2 * Data.MB);
        using var client = await host.Client(Wire.Https2);

        using var first = await client.Download("cached.bin");
        await first.Content.ReadAsByteArrayAsync(Ct);
        var etag = first.Headers.ETag!;

        using var again = await client.Download("cached.bin", r => r.Headers.IfNoneMatch.Add(etag));

        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Equal(2 * Data.MB, await host.DownloadedBytesToday());
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task An_abandoned_download_gives_its_parallel_slot_back(Wire wire)
    {
        await using var host = await FileServeHost.Start(s => s.MaxParallelDownloads = 1);
        var bytes = host.PutFile("one.bin", 16 * Data.MB);
        using var client = await host.Client(wire);

        await client.DownloadAndCutOff("one.bin", Data.MB);

        // The server notices the reset or the closed socket on its next write. Give it a moment,
        // then the slot must be free again - a stuck stream used to hold it until a restart.
        HttpResponseMessage? retry = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            retry = await client.Download("one.bin");
            if (retry.StatusCode != HttpStatusCode.TooManyRequests)
                break;

            retry.Dispose();
            await Task.Delay(100, Ct);
        }

        using (retry)
        {
            Assert.Equal(HttpStatusCode.OK, retry!.StatusCode);
            Assert.Equal(Data.Hash(bytes), Data.Hash(await retry.Content.ReadAsByteArrayAsync(Ct)));
        }
    }
}
