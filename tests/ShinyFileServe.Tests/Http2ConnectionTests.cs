using System.Net;
using ShinyFileServe.Tests.Infrastructure;

namespace ShinyFileServe.Tests;

/// <summary>
/// One HTTP/2 connection carries every request a browser tab makes. Whatever happens to one stream
/// on it - cancelled part way, reset, left unread - must not damage the connection for the rest.
/// </summary>
public class Http2ConnectionTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_download_cancelled_part_way_leaves_the_connection_usable()
    {
        await using var host = await FileServeHost.Start();
        var bytes = host.PutFile("file.bin", 16 * Data.MB);
        using var client = await host.Client(Wire.Https2);

        for (var round = 0; round < 10; round++)
        {
            await client.DownloadAndCutOff("file.bin", Data.MB);

            using var next = await client.Download("file.bin");
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.Equal(Data.Hash(bytes), Data.Hash(await next.Content.ReadAsByteArrayAsync(Ct)));
        }
    }

    [Fact]
    public async Task A_download_cancelled_while_others_run_does_not_break_them()
    {
        await using var host = await FileServeHost.Start();
        var files = Enumerable.Range(0, 4).Select(i => host.PutFile($"k{i}.bin", 12 * Data.MB, seed: i)).ToList();
        host.PutFile("victim.bin", 16 * Data.MB, seed: 99);
        using var client = await host.Client(Wire.Https2);

        var keep = files.Select(async (bytes, i) =>
        {
            using var response = await client.Download($"k{i}.bin");
            await using var stream = await response.Content.ReadAsStreamAsync(Ct);
            using var copy = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer, Ct)) > 0)
            {
                copy.Write(buffer, 0, n);
                await Task.Delay(2, Ct);
            }
            return (bytes, copy.ToArray());
        }).ToList();

        for (var round = 0; round < 5; round++)
            await client.DownloadAndCutOff("victim.bin", 512 * 1024);

        var results = await Task.WhenAll(keep);
        Assert.All(results, r => Assert.Equal(Data.Hash(r.bytes), Data.Hash(r.Item2)));
    }

    [Fact]
    public async Task An_upload_cancelled_part_way_leaves_the_connection_usable()
    {
        await using var host = await FileServeHost.Start();
        using var client = await host.Client(Wire.Https2);
        var bytes = Data.Random(4 * Data.MB);

        for (var round = 0; round < 5; round++)
        {
            var url = await client.TusCreate($"u{round}.bin", bytes.Length);
            await client.TusPatchCutOff(url, 0, bytes, Data.MB);

            using var response = await client.UploadMultipart(null, ($"ok{round}.bin", bytes[..Data.MB]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
