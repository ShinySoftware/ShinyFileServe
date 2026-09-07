using System.Net;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer;
using ShinyFileServe.Core;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server;

namespace ShinyFileServe.App.Services;

/// <summary>
/// The single place the desktop app drives the server and tunnel from. The window, the tray menu
/// and the web UI all end up here, so the three can never show different states or fight over the
/// listener.
/// </summary>
public class ServerController
{
    readonly IFileServeServer server;
    readonly ITunnelController tunnel;
    readonly ISettingsManager settings;
    readonly SessionPolicyHolder sessionPolicy;
    readonly FileServeInitializer initializer;
    readonly ILogger<ServerController> logger;
    readonly SemaphoreSlim gate = new(1, 1);

    bool initialized;

    public ServerController(
        IFileServeServer server,
        ITunnelController tunnel,
        ISettingsManager settings,
        SessionPolicyHolder sessionPolicy,
        FileServeInitializer initializer,
        ILogger<ServerController> logger)
    {
        this.server = server;
        this.tunnel = tunnel;
        this.settings = settings;
        this.sessionPolicy = sessionPolicy;
        this.initializer = initializer;
        this.logger = logger;

        this.server.StateChanged += (_, _) => this.Raise();
        this.server.SessionsChanged += (_, _) => this.Raise();
        this.tunnel.Changed += (_, _) => this.Raise();
        this.settings.Changed += (_, s) =>
        {
            this.sessionPolicy.ApplyRememberWindow(s.RememberMeDays);
            this.Raise();
        };
    }

    public bool IsServing => this.server.IsRunning;
    public HttpServerState State => this.server.State;
    public int ActiveConnections => this.server.ActiveConnections;

    /// <summary>People using the server from elsewhere. This machine's own window is not one.</summary>
    public int ActiveSessionCount => this.server.ActiveSessionCount;

    public TunnelState TunnelState => this.tunnel.State;
    public string? TunnelUrl => this.tunnel.PublicUrl;
    public string? TunnelError => this.tunnel.LastError;
    public bool IsTunnelOpen => this.tunnel.State is TunnelState.Open;

    public ServerSettings Settings => this.settings.Current;

    public string? LastError { get; private set; }

    /// <summary>
    /// The address to hand people: the public tunnel when it is up, otherwise the LAN address.
    /// Null when nothing is listening.
    /// </summary>
    public string? PreferredUrl
    {
        get
        {
            if (this.IsTunnelOpen && this.TunnelUrl is { Length: > 0 } url)
                return url;

            return this.IsServing ? this.LocalUrls.FirstOrDefault() : null;
        }
    }

    public IReadOnlyList<string> LocalUrls =>
        this.IsServing ? NetworkAddresses.LocalUrls(this.settings.Current.Port) : [];

    /// <summary>
    /// The address the console window loads. Loopback deliberately: it is the one connection the
    /// server signs in without a password, and that only holds for a request that never left this
    /// machine.
    /// </summary>
    public string? LoopbackUrl => this.IsServing ? $"http://127.0.0.1:{this.settings.Current.Port}/" : null;

    /// <summary>Raised on any state change, from any thread. Marshal before touching the UI.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// First-run work, then the stored auto-start choices. Safe to call more than once - the window
    /// and the tray both reach for it and whichever gets there first does the work.
    /// </summary>
    public async Task Initialize(CancellationToken ct = default)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            if (this.initialized)
                return;

            this.initialized = true;
            await this.initializer.Initialize(ct);
            this.sessionPolicy.ApplyRememberWindow(this.settings.Current.RememberMeDays);

            // The listener was registered with a placeholder port; move it onto the stored one
            // before anything binds.
            await this.server.Rebind(IPAddress.Any, this.settings.Current.Port, ct);

            if (this.settings.Current.AutoStartServer)
            {
                await this.StartInternal(ct);

                if (this.settings.Current.AutoStartTunnel && this.IsServing)
                    await this.StartTunnelInternal(ct);
            }
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            this.logger.LogError(ex, "Startup failed");
        }
        finally
        {
            this.gate.Release();
            this.Raise();
        }
    }

    public async Task StartServer(CancellationToken ct = default)
    {
        await this.StartInternal(ct);
        this.Raise();
    }

    async Task StartInternal(CancellationToken ct)
    {
        this.LastError = null;
        try
        {
            await this.server.Rebind(IPAddress.Any, this.settings.Current.Port, ct);
            await this.server.Start(ct);
        }
        catch (Exception ex)
        {
            // "Address already in use" is the common one and says enough on its own.
            this.LastError = ex.Message;
            this.logger.LogError(ex, "Could not start the server");
        }
    }

    public async Task StopServer(CancellationToken ct = default)
    {
        // The tunnel forwards to the listener, so it goes first - otherwise it sits reconnecting
        // against a socket that is not coming back.
        await this.tunnel.Stop(ct);
        await this.server.Stop(ct);
        this.Raise();
    }

    public Task ToggleServer(CancellationToken ct = default)
        => this.IsServing ? this.StopServer(ct) : this.StartServer(ct);

    public async Task StartTunnel(CancellationToken ct = default)
    {
        await this.StartTunnelInternal(ct);
        this.Raise();
    }

    async Task StartTunnelInternal(CancellationToken ct)
    {
        this.LastError = null;
        try
        {
            await this.tunnel.Start(ct);
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
        }
    }

    public async Task StopTunnel(CancellationToken ct = default)
    {
        await this.tunnel.Stop(ct);
        this.Raise();
    }

    public Task ToggleTunnel(CancellationToken ct = default)
        => this.IsTunnelOpen || this.TunnelState == TunnelState.Connecting
            ? this.StopTunnel(ct)
            : this.StartTunnel(ct);

    /// <summary>Persists settings and moves the listener if the port changed.</summary>
    public async Task SaveSettings(ServerSettings updated, CancellationToken ct = default)
    {
        var portChanged = this.settings.Current.Port != updated.Port;
        await this.settings.Save(updated, ct);

        if (portChanged)
            await this.server.Rebind(IPAddress.Any, updated.Port, ct);

        this.Raise();
    }

    void Raise() => this.Changed?.Invoke(this, EventArgs.Empty);
}
