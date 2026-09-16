using System.Net;
using System.Net.Sockets;

namespace FastTorrentDownload.Services;

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
}
