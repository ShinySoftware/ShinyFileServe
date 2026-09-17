using System.Net;

namespace ShinyFileServe.Server;

public class FileServeServerOptions
{
    /// <summary>Defaults to loopback. Set to <see cref="IPAddress.Any"/> to publish on the LAN.</summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>The port to listen on. The stored setting replaces this before the server first binds.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Where the cookie-signing key lives. It must survive a restart, otherwise every session is
    /// invalidated whenever the app reopens. Generated on first use.
    /// </summary>
    public string TicketKeyPath { get; set; } = "fileserve.key";

    /// <summary>Largest request body the server will accept, which is the ceiling on any upload.</summary>
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Sign in a caller on the server's own machine without a password. On by default: the desktop
    /// app is a window onto its own server, and whoever is at that machine already owns the process
    /// and the files. Tunnelled connections are excluded whatever this is set to.
    /// </summary>
    public bool TrustLoopback { get; set; } = true;

    /// <summary>
    /// Where installed frontend updates are kept. Null uses a folder named after the app id under local application data.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Host names people may reach the server by, besides IP addresses, loopback, this machine's own name and an open
    /// tunnel's address: a DNS name pointed at this machine, say. Anything else gets a 421.
    /// </summary>
    public ISet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The version of the frontend embedded in this build. Compared against downloads when updates are on.</summary>
    public string FrontendVersion { get; set; } = "1.0.0";

    /// <summary>
    /// A Shiny.AppDeviceBridge.AspNetCore release server to take signed frontend updates from. Null serves the embedded
    /// frontend only. Requires <see cref="FrontendPublicKey"/>.
    /// </summary>
    public Uri? FrontendUpdateServer { get; set; }

    /// <summary>The ECDSA P-256 public key frontend releases are signed with. Compiled in, never read from settings.</summary>
    public string? FrontendPublicKey { get; set; }

    /// <summary>Development only. Leaves stack traces in 500 responses.</summary>
    public bool ShowExceptionDetails { get; set; }
}
