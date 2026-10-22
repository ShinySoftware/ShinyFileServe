using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ShinyFileServe.Core.Services;
using Shiny.AppDeviceBridge;
using Shiny.AppDeviceBridge.Tunnel;
using Shiny.AppDeviceBridge.WebView;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;
using ShinyFileServe.Server.Endpoints;
using ShinyFileServe.Server.PackageFeeds;
using ShinyFileServe.Server.Security;
using ShinyFileServe.Server.Transfers;
using ShinyFileServe.Server.Updates;

namespace ShinyFileServe.Server;

public static class ServerRegistration
{
    /// <summary>
    /// The name of the embedded resource holding the published Blazor WebAssembly frontend. The
    /// server project's build zips <c>ShinyFileServe.Web</c>'s publish output into it.
    /// </summary>
    public const string FrontendResourceName = "ShinyFileServe.Server.wwwroot.zip";

    /// <summary>What the web app host and its release server know this app by. Names the install folder too.</summary>
    public const string AppId = "shinyfileserve";

    /// <summary>
    /// Adds the HTTP server to an existing container. <paramref name="autoStart"/> defaults to false
    /// because the desktop app owns a start/stop toggle - the server follows the user, not the process.
    /// </summary>
    public static IServiceCollection AddFileServeServer(
        this IServiceCollection services,
        Action<FileServeServerOptions> configure,
        bool autoStart = false)
    {
        var options = new FileServeServerOptions();
        configure(options);
        services.AddSingleton(options);
        services.TryAddSingleton<ICurrentUserResolver, CurrentUserResolver>();
        services.TryAddSingleton<ILoopbackIdentity, LoopbackIdentity>();
        services.TryAddSingleton<LoopbackSignInMiddleware>();
        services.TryAddSingleton<ISessionTracker>(sp => new SessionTracker(sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<SessionTrackingMiddleware>();
        services.TryAddSingleton<PasswordChangeGateMiddleware>();
        services.TryAddSingleton<ITransferMonitor>(sp => new TransferMonitor(sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<IBandwidthTracker>(sp => new BandwidthTracker(
            sp.GetRequiredService<IBandwidthMeter>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<BandwidthTracker>>()
        ));
        services.TryAddSingleton<BandwidthMeteringMiddleware>();
        services.TryAddSingleton(sp => new PackageFeedHost(options.PackagesDirectory, sp));
        services.TryAddSingleton<PackageGateMiddleware>();

        // The desktop app registers the real one first; anything else hosting the server gets an
        // updater that never finds anything, so the update endpoints still answer.
        services.TryAddSingleton<IAppUpdater, NoAppUpdater>();

        // A singleton because the cache is the whole point of it - one per request would ask the
        // network the same question on every page of the log.
        services.TryAddSingleton<IHostNameResolver>(sp => new HostNameResolver(sp.GetService<TimeProvider>() ?? TimeProvider.System));

        var protector = TicketProtectorFactory.LoadOrCreate(options.TicketKeyPath);

        // Created eagerly so the AddCookie callback below can hand it the live options object;
        // the desktop app and the admin API then move the session window without a restart.
        var sessionPolicy = new SessionPolicyHolder();
        services.AddSingleton(sessionPolicy);

        services.AddShinyHttpServer(
            http =>
            {
                http.Options.Address = options.Address;
                http.Options.Port = options.Port;
                http.Options.Limits.MaxRequestBodySize = options.MaxUploadBytes;
                http.Options.HideExceptionDetails = !options.ShowExceptionDetails;

                http.AddAuthentication()
                    .AddCookie(o =>
                    {
                        o.Scheme = PrincipalFactory.AuthenticationScheme;
                        o.CookieName = "fileserve.auth";
                        o.Protector = protector;
                        o.ExpireTimeSpan = options.SessionLifetime;
                        o.SlidingExpiration = true;
                        o.ValidateTicketAsync = SessionPolicyHolder.ValidateTicket;
                        sessionPolicy.Cookie = o;
                        o.HttpOnly = true;
                        o.SameSite = SameSiteMode.Strict;

                        // The frontend is a SPA: a 302 to a login page would turn every expired
                        // fetch into an HTML body the client cannot parse. Leave the 401 alone.
                        o.LoginPath = null!;
                        o.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    });

                http.AddAuthorization(o => o.AddPolicy(
                    ClaimTypesEx.AdminRole,
                    p => p.RequireRole(ClaimTypesEx.AdminRole)
                ));

                http.Configure(server =>
                {
                    // First, so it sees the responses nothing else does - a 401 from authentication,
                    // the sign-in page, a static asset - and so anything that wraps the body later
                    // is counted as it goes on the wire rather than as the handler wrote it.
                    server.Use<BandwidthMeteringMiddleware>();

                    server.UseAuthentication();

                    // After authentication so a real cookie always wins, and before authorization
                    // so the identity it grants is the one that gets checked.
                    if (options.TrustLoopback)
                        server.Use<LoopbackSignInMiddleware>();

                    // After the loopback middleware so it can tell this machine's own window from
                    // someone actually connecting, and before routing so it sees every request.
                    server.Use<SessionTrackingMiddleware>();

                    // After authentication, so it knows whose session this is, and ahead of routing
                    // so an expired password is refused by every route at once rather than by each
                    // endpoint remembering to ask.
                    server.Use<PasswordChangeGateMiddleware>();

                    // The feeds' own authentication: a token, or anonymous. After the loopback and
                    // session middleware so whatever identity they settled on is replaced rather than
                    // inherited, and before authorization so the account it names is the one checked.
                    server.Use<PackageGateMiddleware>();

                    server.UseAuthorization();

                    server.MapAuthEndpoints();
                    server.MapAccountEndpoints();
                    server.MapAdminEndpoints();
                    server.MapAdminUsersEndpoints();
                    server.MapAdminSharesEndpoints();
                    server.MapAdminBrowseEndpoints();
                    server.MapAdminBackupEndpoints();
                    server.MapAdminLiveEndpoints();
                    server.MapAdminBandwidthEndpoints();
                    server.MapAdminPackagesEndpoints();
                    server.MapAdminUpdateEndpoints();
                    server.MapFolderEndpoints();
                    server.MapFileEndpoints();
                    server.MapShareLinkEndpoints();
                    server.MapSharedEndpoints();
                    server.MapActivityEndpoints();
                    server.MapPackageFeeds();
                });

                // After Configure, so the bridge server's guard and the web app's files come after the
                // metering, authentication and password gate above - the order the pipeline is built in.
                http.AddAppDeviceBridge(bridge => bridge
                    .Configure(o =>
                    {
                        o.AppId = AppId;
                        o.DataDirectory = options.DataDirectory;

                        // A bind failure is the operator's to see, not something to paper over with a random port
                        // nobody was told about.
                        o.AllowPortFallback = false;

                        // The library lets any caller reach the bridges in a debug build, for a browser on a developer's desk.
                        // This server is on the LAN by design, so a debug build keeps the same rules as a release one.
                        o.AllowAnyCallerInDebug = false;

                        // No page here calls device bridges, so the built-in settings and files roots stay off and the
                        // default policy - this device only - guards the few host routes that remain.
                        o.EnableSettings = false;
                        o.EnableFiles = false;

                        // People reach a LAN server by the machine's name as often as by its address. Anything else is
                        // refused with a 421, which is what keeps a hostile site from rebinding its own name onto it.
                        foreach (var name in HostNames.ForThisMachine())
                            o.AllowedHosts.Add(name);

                        foreach (var name in options.AllowedHosts)
                            o.AllowedHosts.Add(name);
                    })
                    .AddTunnel());

                // The SPA shell itself is public - it only ever shows a login form until /api/auth/me
                // answers. Everything with data behind it is [Authorize]d above. Left out entirely when the
                // assembly was built with SkipFrontendBuild, so the API still runs without it.
                if (HasFrontend())
                {
                    http.AddAppDeviceBridge(_ => { }, o =>
                    {
                        o.UseBaseline(typeof(ServerRegistration).Assembly, FrontendResourceName, options.FrontendVersion);
                        if (options.FrontendUpdateServer is { } updateServer)
                            o.UpdateProvider = new ReleaseServerUpdateProvider(updateServer, options.FrontendPublicKey!);

                        // Remote users are the point of the app, and so is a browser on the machine itself: the
                        // portal has no launch session to hand either of them, and needs none.
                        o.ServeWebAppRemotely = true;
                        o.ServeWebAppLocally = true;
                        o.OnPrepareResponse = FrontendCachePolicy.Apply;
                    });
                }
            },
            autoStart
        );

        services.TryAddSingleton<IFileServeServer, FileServeServerFacade>();
        return services;
    }

    /// <summary>
    /// Whether this assembly carries a real frontend. False when it was built with
    /// <c>SkipFrontendBuild</c>, whose placeholder zip has no index.html to serve.
    /// </summary>
    public static bool HasFrontend()
    {
        var assembly = typeof(ServerRegistration).Assembly;
        if (!assembly.GetManifestResourceNames().Contains(FrontendResourceName))
            return false;

        try
        {
            using var stream = assembly.GetManifestResourceStream(FrontendResourceName)!;
            using var zip = new System.IO.Compression.ZipArchive(stream);
            return zip.Entries.Any(x => x.FullName.Equals("index.html", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

static class TicketProtectorFactory
{
    /// <summary>
    /// The cookie-signing key has to outlive the process, otherwise every restart silently signs
    /// everyone out. Stored beside the database, 0600 where the platform supports it.
    /// </summary>
    public static TicketProtector LoadOrCreate(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!String.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        byte[] key;
        if (File.Exists(full))
        {
            key = File.ReadAllBytes(full);
            if (key.Length >= 32)
                return new TicketProtector(key, []);
        }

        key = TicketProtector.CreateKey();
        File.WriteAllBytes(full, key);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return new TicketProtector(key, []);
    }
}

/// <summary>A small, UI-friendly face on the server: state, address, and a toggle.</summary>
public interface IFileServeServer
{
    HttpServerState State { get; }
    bool IsRunning { get; }
    int ActiveConnections { get; }

    /// <summary>People using the server from elsewhere. This machine's own window is not one.</summary>
    int ActiveSessionCount { get; }

    IReadOnlyList<ActiveSession> ActiveSessions { get; }

    /// <summary>Fires on every transition. Marshal to the UI thread yourself - it is raised off a background thread.</summary>
    event EventHandler<HttpServerState>? StateChanged;

    /// <summary>Raised when somebody arrives or ages out. Also off a background thread.</summary>
    event EventHandler? SessionsChanged;

    Task Start(CancellationToken ct = default);
    Task Stop(CancellationToken ct = default);
    Task Restart(CancellationToken ct = default);

    /// <summary>
    /// Rebinds to a new address and port. The listener is torn down and rebuilt rather than
    /// restarted in place, because the socket is bound during start and nowhere else.
    /// </summary>
    Task Rebind(IPAddress address, int port, CancellationToken ct = default);
}

class FileServeServerFacade : IFileServeServer, IDisposable
{
    readonly HttpServer server;
    readonly ISessionTracker sessions;
    readonly WebAppHost? webApp;

    public FileServeServerFacade(HttpServer server, ISessionTracker sessions, IServiceProvider services)
    {
        this.server = server;
        this.sessions = sessions;
        this.webApp = services.GetService<WebAppHost>();
        this.server.StateChanged += this.OnStateChanged;
        this.sessions.Changed += this.OnSessionsChanged;
    }

    void OnSessionsChanged(object? sender, EventArgs e) => this.SessionsChanged?.Invoke(this, EventArgs.Empty);

    void OnStateChanged(object? sender, HttpServerState e) => this.StateChanged?.Invoke(this, e);

    public HttpServerState State => this.server.State;
    public bool IsRunning => this.server.IsRunning;
    public int ActiveConnections => this.server.ActiveConnections;
    public int ActiveSessionCount => this.sessions.ActiveCount;
    public IReadOnlyList<ActiveSession> ActiveSessions => this.sessions.Active;

    public event EventHandler<HttpServerState>? StateChanged;
    public event EventHandler? SessionsChanged;

    /// <summary>
    /// Through the web app host when there is a frontend: it picks the build to serve - the embedded one, or a newer
    /// download - before the listener opens, and runs the update check when one is configured.
    /// </summary>
    public Task Start(CancellationToken ct = default)
        => this.webApp is { } host ? host.StartAsync(ct) : this.server.StartAsync(ct);

    public async Task Stop(CancellationToken ct = default)
    {
        await this.server.StopAsync(ct);

        // Nobody is connected to a closed socket, and leaving stale entries would keep the tray lit
        // for five minutes after the server went down.
        this.sessions.Clear();
    }
    public Task Restart(CancellationToken ct = default) => this.server.RestartAsync(ct);

    public async Task Rebind(IPAddress address, int port, CancellationToken ct = default)
    {
        var wasRunning = this.server.IsRunning;
        if (wasRunning)
            await this.server.StopAsync(ct);

        this.server.Options.Address = address;
        this.server.Options.Port = port;

        if (wasRunning)
            await this.Start(ct);
    }

    public void Dispose()
    {
        this.server.StateChanged -= this.OnStateChanged;
        this.sessions.Changed -= this.OnSessionsChanged;
    }
}
