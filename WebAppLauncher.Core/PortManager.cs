using System.Net;
using System.Net.NetworkInformation;

namespace WebAppLauncher.Core;

public sealed class PortManager
{
    public const int FirstPort = 52000;
    public const int LastPort = 52999;

    public int AllocatePort(IEnumerable<InstalledApp> installedApps)
    {
        var usedByApps = installedApps.Select(app => app.Manifest.Network.Port).ToHashSet();
        for (var port = FirstPort; port <= LastPort; port++)
        {
            if (!usedByApps.Contains(port) && !IsPortInUse(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("No available port in 52000..52999.");
    }

    public bool IsPortInUse(int port)
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        return properties.GetActiveTcpListeners().Any(endpoint =>
            endpoint.Port == port &&
            (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)));
    }
}
