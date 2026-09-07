using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.StaticFiles;
using ShinyFileServe.Server.Endpoints;
using ShinyFileServe.Server.Security;

namespace ShinyFileServe.Server;

public static class ServerRegistration
{
    /// <summary>
    /// The name of the embedded resource holding the published Blazor WebAssembly frontend. The
    /// server project's build zips <c>ShinyFileServe.Web</c>'s publish output into it.
    /// </summary>
    public const string FrontendResourceName = "ShinyFileServe.Server.wwwroot.zip";

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
        services.TryAddSingleton<ITunnelController, TunnelController>();
        services.TryAddSingleton<ILoopbackIdentity, LoopbackIdentity>();
        services.TryAddSingleton<LoopbackSignInMiddleware>();

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
                    server.UseAuthentication();

                    // After authentication so a real cookie always wins, and before authorization
                    // so the identity it grants is the one that gets checked.
                    if (options.TrustLoopback)
                        server.Use<LoopbackSignInMiddleware>();

                    server.UseAuthorization();

                    server.MapAuthEndpoints();
                    server.MapAdminEndpoints();
                    server.MapAdminUsersEndpoints();
                    server.MapAdminSharesEndpoints();
                    server.MapFolderEndpoints();
                    server.MapFileEndpoints();
                    server.MapActivityEndpoints();

                    // The SPA shell itself is public - it only ever shows a login form until
                    // /api/auth/me answers. Everything with data behind it is [Authorize]d above.
                    var frontend = TryOpenFrontend();
                    if (frontend is not null)
                        server.UseBlazorWebAssembly(frontend);
                });
            },
            autoStart
        );

        services.TryAddSingleton<IFileServeServer, FileServeServerFacade>();
        return services;
    }

    /// <summary>
    /// The published frontend, read straight out of this assembly's embedded zip. Returns null when
    /// the assembly was built with <c>SkipFrontendBuild</c>, so a head can still run its own UI.
    /// </summary>
    public static IStaticFileSource? TryOpenFrontend()
    {
        var assembly = typeof(ServerRegistration).Assembly;
        var names = assembly.GetManifestResourceNames();
        if (!names.Contains(FrontendResourceName))
            return null;

        try
        {
            var source = new ZipFileSource(assembly, FrontendResourceName, basePath: "");
            return source.Paths.Any(p => p.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                ? source
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
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
    string? ListenUrl { get; }
    IReadOnlyList<string> ListenUrls { get; }
    int ActiveConnections { get; }

    /// <summary>Fires on every transition. Marshal to the UI thread yourself - it is raised off a background thread.</summary>
    event EventHandler<HttpServerState>? StateChanged;

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

    public FileServeServerFacade(HttpServer server)
    {
        this.server = server;
        this.server.StateChanged += this.OnStateChanged;
    }

    void OnStateChanged(object? sender, HttpServerState e) => this.StateChanged?.Invoke(this, e);

    public HttpServerState State => this.server.State;
    public bool IsRunning => this.server.IsRunning;
    public string? ListenUrl => this.server.IsRunning ? this.server.ListenUrl : null;
    public IReadOnlyList<string> ListenUrls => this.server.IsRunning ? this.server.ListenUrls : [];
    public int ActiveConnections => this.server.ActiveConnections;

    public event EventHandler<HttpServerState>? StateChanged;

    public Task Start(CancellationToken ct = default) => this.server.StartAsync(ct);
    public Task Stop(CancellationToken ct = default) => this.server.StopAsync(ct);
    public Task Restart(CancellationToken ct = default) => this.server.RestartAsync(ct);

    public async Task Rebind(IPAddress address, int port, CancellationToken ct = default)
    {
        var wasRunning = this.server.IsRunning;
        if (wasRunning)
            await this.server.StopAsync(ct);

        this.server.Options.Address = address;
        this.server.Options.Port = port;

        if (wasRunning)
            await this.server.StartAsync(ct);
    }

    public void Dispose() => this.server.StateChanged -= this.OnStateChanged;
}
