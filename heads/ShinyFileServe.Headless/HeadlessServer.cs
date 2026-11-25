using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shiny.AppDeviceBridge.Tunnel;
using Shiny.Net.HttpServer;
using ShinyFileServe.Core;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server;
using ShinyFileServe.Server.Security;

namespace ShinyFileServe.Headless;

/// <summary>
/// What ServerController does for the desktop app, for a process with nobody at it. The server always
/// starts - there is no tray to start it from - and always on the port the host was given, because in
/// a container that is the port Docker publishes and the stored one would only point at nothing.
///
/// Stopping the server from the portal ends the process. Nothing could start it again from inside,
/// since the portal is served by the listener that just closed, so the container's restart policy
/// brings it back instead. Not straight away: restoring a backup stops the listener for the swap and
/// starts it again, and that must be allowed to finish.
/// </summary>
class HeadlessServer(
    IFileServeServer server,
    AppDeviceBridgeTunnel tunnel,
    ISettingsManager settings,
    SessionPolicyHolder sessionPolicy,
    AllowedHostsHolder allowedHosts,
    FileServeInitializer initializer,
    IUserManager users,
    SetupCode setupCode,
    ServerTls tls,
    HeadlessOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<HeadlessServer> logger
) : BackgroundService
{
    static readonly TimeSpan StoppedGrace = TimeSpan.FromSeconds(30);

    bool stopping;
    CancellationTokenSource? stoppedWatch;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await initializer.Initialize(stoppingToken);

        // The stored settings were only just read, and loading them raises nothing.
        sessionPolicy.ApplyRememberWindow(settings.Current.RememberMeDays);
        allowedHosts.Apply(settings.Current.AllowedHosts);
        settings.Changed += (_, s) =>
        {
            sessionPolicy.ApplyRememberWindow(s.RememberMeDays);
            allowedHosts.Apply(s.AllowedHosts);
        };

        if (settings.Current.Port != options.Port)
            logger.LogInformation("Listening on {Port} as the host asks. The port in the portal's settings ({Stored}) is not used here", options.Port, settings.Current.Port);

        server.StateChanged += this.OnStateChanged;

        // Both ports are the host's: they are what the container publishes.
        tls.FixByHost(options.Port, options.HttpsPort);
        await server.Rebind(IPAddress.Any, options.Port, stoppingToken);
        await server.Start(stoppingToken);
        logger.LogInformation("FileServe is serving http on port {Port}. Data is in {Data}", options.Port, HeadlessPaths.DataDirectory);

        if (tls.ActivePort is { } https)
            logger.LogInformation("And https on port {HttpsPort}, on its own certificate - browsers will ask to trust it once", https);
        else if (tls.LastError is { } problem)
            logger.LogError("HTTPS is off: {Problem}", problem);

        if (await users.NeedsSetup(stoppingToken))
        {
            logger.LogWarning(
                "No accounts yet. Open the portal and create the administrator with this setup code: {Code}",
                setupCode.Code
            );
        }

        if (settings.Current.AutoStartTunnel)
        {
            // Failures land in the tunnel's own state, which the portal shows.
            var url = await tunnel.StartAsync(settings.Current.PinggyToken, stoppingToken);
            logger.LogInformation("Public tunnel: {Url}", url?.GetLeftPart(UriPartial.Authority) ?? tunnel.LastError ?? "not open");
        }
    }

    void OnStateChanged(object? sender, HttpServerState state)
    {
        this.stoppedWatch?.Cancel();
        this.stoppedWatch = null;

        if (state is HttpServerState.Stopped && !this.stopping)
        {
            var watch = new CancellationTokenSource();
            this.stoppedWatch = watch;
            _ = this.ExitIfStillStopped(watch.Token);
        }
    }

    async Task ExitIfStillStopped(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StoppedGrace, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!server.IsRunning && !this.stopping)
        {
            logger.LogWarning("The server was stopped from the portal. Exiting so the host can start it again");
            lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        this.stopping = true;
        server.StateChanged -= this.OnStateChanged;

        await tunnel.StopAsync(cancellationToken);
        await server.Stop(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>What the host decides rather than the database: read from the environment at startup.</summary>
class HeadlessOptions
{
    public int Port { get; init; } = 8080;

    /// <summary>0 is off.</summary>
    public int HttpsPort { get; init; } = 8443;
}
