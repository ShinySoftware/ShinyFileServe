using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Ssh;
using ShinyFileServe.Core.Services;

namespace ShinyFileServe.Server;

public enum TunnelState
{
    Off,
    Connecting,
    Open,
    Reconnecting,
    Failed
}

/// <summary>
/// The public tunnel. Built on demand rather than registered up front, because the pinggy token is
/// a setting the operator can change at any time from the desktop app or the web UI, and a token
/// baked in at registration would be stale the moment they did.
/// </summary>
public interface ITunnelController
{
    TunnelState State { get; }

    /// <summary>The https address to hand out. Null until the tunnel reports one.</summary>
    string? PublicUrl { get; }

    string? LastError { get; }

    /// <summary>Fires on connect, reconnect, address change and failure. Raised off a background thread.</summary>
    event EventHandler? Changed;

    Task Start(CancellationToken ct = default);
    Task Stop(CancellationToken ct = default);
}

public class TunnelController(
    HttpServer server,
    ISettingsManager settings,
    ILoggerFactory loggerFactory,
    ILogger<TunnelController> logger
) : ITunnelController, IAsyncDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    QuickTunnel? tunnel;

    public TunnelState State { get; private set; } = TunnelState.Off;
    public string? PublicUrl { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler? Changed;

    public async Task Start(CancellationToken ct = default)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            if (this.tunnel is not null)
                return;

            if (!server.IsRunning)
                throw new InvalidOperationException("Start the server before opening a tunnel to it.");

            this.LastError = null;
            this.State = TunnelState.Connecting;
            this.Raise();

            // A blank token gives an anonymous pinggy tunnel: it works, but it expires after an hour
            // and takes a new address on every reconnect. The token is passed as the subdomain
            // argument, which is how this provider carries authentication.
            var token = String.IsNullOrWhiteSpace(settings.Current.PinggyToken)
                ? null
                : settings.Current.PinggyToken.Trim();

            var created = QuickTunnel.For(server, QuickTunnelHost.Pinggy, token, loggerFactory: loggerFactory);
            created.PropertyChanged += this.OnTunnelPropertyChanged;
            this.tunnel = created;

            var url = await created.StartAsync(ct);

            // StartAsync returning null means it connected but never learned an address - a live
            // tunnel nobody can reach. Treat it as a failure rather than quiet success.
            if (String.IsNullOrEmpty(url) && String.IsNullOrEmpty(created.PublicUrl))
            {
                this.LastError = created.LastError ?? "The tunnel connected but never reported an address.";
                this.State = TunnelState.Failed;
                logger.LogWarning("Tunnel started without an address: {Error}", this.LastError);
            }
            else
            {
                this.PublicUrl = url ?? created.PublicUrl;
                this.State = TunnelState.Open;
                logger.LogInformation("Tunnel open at {Url}", this.PublicUrl);
            }
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            this.State = TunnelState.Failed;
            await this.Teardown();
            logger.LogError(ex, "Tunnel failed to start");
        }
        finally
        {
            this.gate.Release();
            this.Raise();
        }
    }

    public async Task Stop(CancellationToken ct = default)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            if (this.tunnel is { } active)
                await active.StopAsync(ct);

            await this.Teardown();
            this.State = TunnelState.Off;
            this.PublicUrl = null;
        }
        finally
        {
            this.gate.Release();
            this.Raise();
        }
    }

    void OnTunnelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not QuickTunnel source)
            return;

        // A free tunnel reassigns its address on every reconnect, so the URL is followed rather
        // than read once at start.
        this.PublicUrl = source.PublicUrl;
        this.LastError = source.LastError;
        this.State = source.State switch
        {
            QuickTunnelState.Connected => TunnelState.Open,
            QuickTunnelState.Connecting => TunnelState.Connecting,
            QuickTunnelState.Reconnecting => TunnelState.Reconnecting,
            QuickTunnelState.Failed => TunnelState.Failed,
            _ => TunnelState.Off
        };
        this.Raise();
    }

    async Task Teardown()
    {
        if (this.tunnel is not { } active)
            return;

        active.PropertyChanged -= this.OnTunnelPropertyChanged;
        this.tunnel = null;
        await active.DisposeAsync();
    }

    void Raise() => this.Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        await this.Teardown();
        this.gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
