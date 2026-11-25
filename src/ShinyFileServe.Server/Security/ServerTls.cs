using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer;

namespace ShinyFileServe.Server.Security;

/// <summary>
/// HTTPS beside the plain listener, on a certificate this server makes for itself. Browsers will
/// only run the portal on a secure connection - https, or the machine itself - so a LAN server that
/// speaks nothing but http is a server nobody else can use. Nobody issues a public certificate for
/// a 192.168 address, so the server signs its own: each browser warns once, the person accepts it,
/// and from then on the connection is secure.
///
/// Plain http stays where it was. The desktop window loads it over loopback, which browsers already
/// trust and a self-signed certificate would only get in the way of; a reverse proxy forwards to it
/// and does its own TLS; and package clients refuse certificates nobody vouched for.
/// </summary>
public class ServerTls(FileServeServerOptions options, ILogger<ServerTls> logger)
{
    readonly Lock gate = new();
    X509Certificate2? certificate;

    /// <summary>
    /// The port the host asked for. Null or 0 turns HTTPS off. Applied the next time the listener
    /// starts, like the plain port.
    /// </summary>
    public int? RequestedPort { get; private set; } = options.HttpsPort;

    /// <summary>
    /// True when the environment chose the port (a container publishing it) rather than the stored
    /// settings - the portal shows it but does not offer to change it.
    /// </summary>
    public bool IsFixedByHost { get; private set; }

    /// <summary>The port HTTPS is actually being served on, or null when it is off or failed to bind.</summary>
    public int? ActivePort { get; internal set; }

    /// <summary>Why HTTPS is not being served although it was asked for.</summary>
    public string? LastError { get; internal set; }

    /// <summary>
    /// The addresses to hand people when the host fixed the ports - a container, where the addresses
    /// it can see for itself are its own internal ones and reach nobody. Empty otherwise, or when the
    /// host named none, in which case the machine's own addresses are the best there is.
    /// </summary>
    public IReadOnlyList<string> HostUrls()
    {
        if (!this.IsFixedByHost || this.HostPort is not { } port)
            return [];

        return options.CertificateNames
            .Concat(options.AllowedHosts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => IPAddress.TryParse(name, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{name}]" : name)
            .Select(name => this.ActivePort is { } https ? $"https://{name}:{https}" : $"http://{name}:{port}")
            .ToList();
    }

    /// <summary>From the stored settings. Ignored once the host has fixed the port.</summary>
    public void Request(int? port)
    {
        if (!this.IsFixedByHost)
            this.RequestedPort = port is > 0 ? port : null;
    }

    /// <summary>
    /// The plain port the host fixed alongside, so the portal can show the one actually in use
    /// rather than the stored setting the host is ignoring. Null when the settings decide.
    /// </summary>
    public int? HostPort { get; private set; }

    /// <summary>The host's choice of both ports, which the stored settings cannot override.</summary>
    public void FixByHost(int port, int? httpsPort)
    {
        this.HostPort = port;
        this.RequestedPort = httpsPort is > 0 ? httpsPort : null;
        this.IsFixedByHost = true;
    }

    /// <summary>
    /// Loaded from beside the database, or made and stored there on first use - stored so that a
    /// browser that accepted it once is not asked again after every restart.
    /// </summary>
    public X509Certificate2 Certificate
    {
        get
        {
            lock (this.gate)
            {
                if (this.certificate is null)
                {
                    var path = Path.GetFullPath(options.CertificatePath);
                    var directory = Path.GetDirectoryName(path);
                    if (!String.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    var wanted = options.AllowedHosts.Concat(options.CertificateNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var loaded = this.Load(path, wanted);

                    // Stored before a name was added - a new host name, a container given its LAN
                    // address. Made again, because a certificate missing the address in the browser
                    // can never be trusted however many times somebody clicks through.
                    var missing = Missing(loaded, wanted);
                    if (missing.Count > 0)
                    {
                        logger.LogInformation("Making a new HTTPS certificate to add {Names}", String.Join(", ", missing));
                        loaded.Dispose();
                        File.Delete(path);
                        loaded = this.Load(path, wanted);
                    }

                    this.certificate = loaded;
                    logger.LogInformation(
                        "HTTPS certificate {Thumbprint}, valid until {Expires:yyyy-MM-dd}",
                        this.certificate.Thumbprint,
                        this.certificate.NotAfter
                    );
                }
                return this.certificate;
            }
        }
    }

    X509Certificate2 Load(string path, IReadOnlyList<string> names)
        => ServerCertificate.CreateOrLoad(path, o =>
        {
            o.CommonName = "FileServe";

            // Localhost, the machine's name and every address it can see are added already.
            foreach (var name in names)
            {
                if (IPAddress.TryParse(name, out var address))
                    o.IPAddresses.Add(address);
                else
                    o.DnsNames.Add(name);
            }
        });

    static List<string> Missing(X509Certificate2 certificate, IEnumerable<string> names)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        var dns = san?.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var ips = san?.EnumerateIPAddresses().ToHashSet() ?? [];

        return names
            .Where(n => IPAddress.TryParse(n, out var address) ? !ips.Contains(address) : !dns.Contains(n))
            .ToList();
    }
}
