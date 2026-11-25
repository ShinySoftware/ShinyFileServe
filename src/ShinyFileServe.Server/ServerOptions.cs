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

    /// <summary>
    /// Serve HTTPS on this port as well, on a self-signed certificate kept at
    /// <see cref="CertificatePath"/>. Null leaves HTTPS off. The host can change it at runtime
    /// through <see cref="Security.ServerTls"/>.
    /// </summary>
    public int? HttpsPort { get; set; }

    /// <summary>Where the self-signed certificate is kept. Made on first use and reused after.</summary>
    public string CertificatePath { get; set; } = "server.pfx";

    /// <summary>
    /// Addresses and names the certificate must carry besides the ones this machine can see for
    /// itself. A container sees only its own internal address, not the host's LAN address people
    /// type - and a certificate that does not name the address in the browser can never be trusted.
    /// </summary>
    public ISet<string> CertificateNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Largest request body the server will accept. File uploads are exempt - they stream to disk and
    /// are bounded by each share's per-file limit and quotas instead - so this guards everything else.
    /// </summary>
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

    /// <summary>
    /// Where the NuGet feed and npm registry keep their packages - <c>nuget</c> and <c>npm</c> folders
    /// underneath. Both are plain folder layouts that can be copied, seeded or backed up by hand.
    /// </summary>
    public string PackagesDirectory { get; set; } = "packages";

    /// <summary>
    /// Where resumable uploads are kept until their last byte arrives and they are moved into their
    /// share. Unfinished ones are swept away after <see cref="ResumableUploadExpiry"/>.
    /// </summary>
    public string UploadsDirectory { get; set; } = "uploads";

    /// <summary>How long an unfinished resumable upload waits for its next byte before it is thrown away.</summary>
    public TimeSpan ResumableUploadExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Development only. Leaves stack traces in 500 responses.</summary>
    public bool ShowExceptionDetails { get; set; }
}
