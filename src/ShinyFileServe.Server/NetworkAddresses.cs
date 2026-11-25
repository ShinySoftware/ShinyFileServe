using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ShinyFileServe.Server;

public static class NetworkAddresses
{
    /// <summary>
    /// The addresses that actually reach this machine from elsewhere. Loopback is left out - it is
    /// never the answer to "what do I send my colleague?". The HTTPS address when HTTPS is being
    /// served, since a browser elsewhere cannot run the portal over plain http.
    /// </summary>
    public static IReadOnlyList<string> LocalUrls(int port, int? httpsPort = null)
        => NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => httpsPort is { } secure ? $"https://{a}:{secure}" : $"http://{a}:{port}")
            .Distinct()
            .ToList();
}
