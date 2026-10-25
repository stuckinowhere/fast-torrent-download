using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FastTorrentDownload.Services;

public sealed record LocalNetworkGuideInfo(
    string HostName,
    string LocalIpv4,
    string GatewayIpv4,
    string AdapterName,
    string MacAddress,
    int ListenPort);

public static class NetworkBinding
{
    public static IPAddress ResolveListenAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Trim() is "0.0.0.0" or "any")
        {
            return IPAddress.Any;
        }

        if (!IPAddress.TryParse(address.Trim(), out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Listener address must be an IPv4 address such as 192.168.1.25, or 0.0.0.0 for all interfaces.");
        }

        return parsed;
    }

    public static LocalNetworkGuideInfo DetectLocalNetworkInfo(int listenPort)
    {
        var hostName = Environment.MachineName;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                var gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                var unicast = props.UnicastAddresses
                    .Select(u => u.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                if (gateway is null || unicast is null)
                {
                    continue;
                }

                var macBytes = nic.GetPhysicalAddress().GetAddressBytes();
                var mac = macBytes.Length > 0
                    ? string.Join(":", macBytes.Select(b => b.ToString("X2")))
                    : "unknown";
                return new LocalNetworkGuideInfo(
                    hostName,
                    unicast.ToString(),
                    gateway.ToString(),
                    nic.Name,
                    mac,
                    listenPort);
            }
        }
        catch
        {
            // Fall back to safe placeholders if interface enumeration is restricted.
        }

        return new LocalNetworkGuideInfo(hostName, "192.168.x.x", "192.168.x.1", "Ethernet / Wi-Fi", "unknown", listenPort);
    }
}
