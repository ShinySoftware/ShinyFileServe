using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ShinyFileServe.Server;

public static class NetworkAddresses
{
    /// <summary>
    /// The addresses that actually reach this machine from elsewhere. Loopback is left out - it is
    /// never the answer to "what do I send my colleague?".
    /// </summary>
    public static IReadOnlyList<string> LocalUrls(int port)
        => NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => $"http://{a}:{port}")
            .Distinct()
            .ToList();
}
