using System.Net;
using System.Net.Sockets;

namespace ShinyFileServe.Tests.Infrastructure;

/// <summary>
/// A TCP relay in front of the server that can make its clients vanish the way they do in real life:
/// a laptop lid closes, Wi-Fi hands over to another network, a NAT forgets the mapping. Nothing is
/// closed and nothing is reset - the bytes simply stop, in both directions, and the server is left
/// holding a connection whose other end is gone.
/// </summary>
public sealed class VanishingProxy : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly int target;
    readonly CancellationTokenSource stopped = new();
    readonly List<(TcpClient Client, TcpClient Upstream)> pairs = [];
    volatile bool vanished;

    public VanishingProxy(int targetPort)
    {
        this.target = targetPort;
        this.listener.Start();
        _ = this.AcceptLoop();
    }

    public int Port => ((IPEndPoint)this.listener.LocalEndpoint).Port;

    /// <summary>From now on nothing moves either way on any connection through the proxy, and nothing is closed.</summary>
    public void Vanish() => this.vanished = true;

    async Task AcceptLoop()
    {
        while (!this.stopped.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await this.listener.AcceptTcpClientAsync(this.stopped.Token);
            }
            catch (Exception)
            {
                return;
            }

            var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, this.target);
            lock (this.pairs)
                this.pairs.Add((client, upstream));

            _ = this.Pump(client.GetStream(), upstream.GetStream());
            _ = this.Pump(upstream.GetStream(), client.GetStream());
        }
    }

    async Task Pump(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!this.stopped.IsCancellationRequested)
            {
                // Once vanished, stop reading as well as writing: the sender's window fills and
                // its writes stall, exactly as with a peer that is no longer there.
                if (this.vanished)
                {
                    await Task.Delay(Timeout.Infinite, this.stopped.Token);
                }

                var read = await from.ReadAsync(buffer, this.stopped.Token);
                if (read == 0)
                    return;

                if (this.vanished)
                {
                    await Task.Delay(Timeout.Infinite, this.stopped.Token);
                }

                await to.WriteAsync(buffer.AsMemory(0, read), this.stopped.Token);
            }
        }
        catch (Exception)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        this.stopped.Cancel();
        this.listener.Stop();
        lock (this.pairs)
        {
            foreach (var (client, upstream) in this.pairs)
            {
                client.Dispose();
                upstream.Dispose();
            }
        }
        return ValueTask.CompletedTask;
    }
}
