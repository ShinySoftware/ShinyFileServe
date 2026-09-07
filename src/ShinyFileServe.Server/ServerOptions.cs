using System.Net;

namespace ShinyFileServe.Server;

public class FileServeServerOptions
{
    /// <summary>Defaults to loopback. Set to <see cref="IPAddress.Any"/> to publish on the LAN.</summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>0 lets the OS pick; read the real one back from <c>FileServeServer.ListenUrl</c>.</summary>
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

    /// <summary>Development only. Leaves stack traces in 500 responses.</summary>
    public bool ShowExceptionDetails { get; set; }
}
