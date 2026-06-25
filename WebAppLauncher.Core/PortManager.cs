using System.Net;
using System.Net.NetworkInformation;

namespace WebAppLauncher.Core;

public sealed class PortManager
{
    public const int FirstPort = 52000;
    public const int LastPort = 52999;

    private readonly Func<int, bool>? isPortInUse;
    private readonly HashSet<int> reservedPorts = [];
    private readonly object sync = new();

    public PortManager(Func<int, bool>? isPortInUse = null)
    {
        this.isPortInUse = isPortInUse;
    }

    public int AllocatePort()
    {
        lock (sync)
        {
            var occupiedPorts = isPortInUse is null
                ? GetOccupiedPorts().ToHashSet()
                : null;
            for (var port = FirstPort; port <= LastPort; port++)
            {
                var occupied = occupiedPorts?.Contains(port) ?? isPortInUse!(port);
                if (!reservedPorts.Contains(port) && !occupied)
                {
                    reservedPorts.Add(port);
                    return port;
                }
            }
        }

        throw new InvalidOperationException("No available port in 52000..52999.");
    }

    public void ReleasePort(int port)
    {
        lock (sync)
        {
            reservedPorts.Remove(port);
        }
    }

    public static IReadOnlyList<int> GetOccupiedPorts()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        return properties.GetActiveTcpListeners()
            .Where(endpoint =>
                endpoint.Port is >= FirstPort and <= LastPort &&
                (IPAddress.IsLoopback(endpoint.Address) ||
                 endpoint.Address.Equals(IPAddress.Any) ||
                 endpoint.Address.Equals(IPAddress.IPv6Any)))
            .Select(endpoint => endpoint.Port)
            .Distinct()
            .Order()
            .ToArray();
    }

}
