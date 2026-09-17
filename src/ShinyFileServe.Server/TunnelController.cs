using System.ComponentModel;
using Shiny.AppDeviceBridge.Tunnel;
using Shiny.Net.HttpServer;
using ShinyFileServe.Core.Services;
using BridgeTunnelState = Shiny.AppDeviceBridge.Tunnel.TunnelState;

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
/// The public tunnel's on/off switch. The tunnel itself is Shiny.AppDeviceBridge's: it accepts only its own public host,
/// and everything through it is a remote caller. What stays here is the app's part - the pinggy token is a setting the
/// operator can change at any time, so it is read at every start, and a tunnel is only opened onto a running server.
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

public class TunnelController : ITunnelController, IDisposable
{
    readonly AppDeviceBridgeTunnel tunnel;
    readonly HttpServer server;
    readonly ISettingsManager settings;

    public TunnelController(AppDeviceBridgeTunnel tunnel, HttpServer server, ISettingsManager settings)
    {
        this.tunnel = tunnel;
        this.server = server;
        this.settings = settings;
        this.tunnel.PropertyChanged += this.OnTunnelChanged;
    }

    public TunnelState State => this.tunnel.State switch
    {
        BridgeTunnelState.Connecting => TunnelState.Connecting,
        BridgeTunnelState.Connected => TunnelState.Open,
        BridgeTunnelState.Reconnecting => TunnelState.Reconnecting,
        BridgeTunnelState.Failed => TunnelState.Failed,
        _ => TunnelState.Off
    };

    // A free tunnel reassigns its address on every reconnect, so this is read live rather than kept from the start.
    public string? PublicUrl => this.tunnel.PublicUrl?.AbsoluteUri.TrimEnd('/');

    public string? LastError => this.tunnel.LastError;

    public event EventHandler? Changed;

    public async Task Start(CancellationToken ct = default)
    {
        // The tunnel could serve a stopped server, but here it forwards to the same portal people reach on the LAN, and
        // the operator's server switch is meant to take all of it down.
        if (!this.server.IsRunning)
            throw new InvalidOperationException("Start the server before opening a tunnel to it.");

        // A blank token gives an anonymous pinggy tunnel: it works, but it expires after an hour and takes a new address
        // on every reconnect. pinggy carries the token where other hosts carry a subdomain.
        await this.tunnel.StartAsync(this.settings.Current.PinggyToken, ct);
    }

    public Task Stop(CancellationToken ct = default) => this.tunnel.StopAsync(ct);

    void OnTunnelChanged(object? sender, PropertyChangedEventArgs args) => this.Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        this.tunnel.PropertyChanged -= this.OnTunnelChanged;
        GC.SuppressFinalize(this);
    }
}
