using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace FastTorrentDownload.Services;

public static class DhtBootstrapCache
{
    private static readonly (string Host, int Port)[] Routers =
    [
        ("dht.transmissionbt.com", 6881),
        ("dht.libtorrent.org", 25401),
        ("router.utorrent.com", 6881),
        ("router.bittorrent.com", 6881),
    ];

    public static async Task EnsureSeededAsync(string cacheDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            // MonoTorrent 3.0.2 loads dht_nodes.cache as FLAT bytes sliced into 26-byte
            // compact nodes (Node.FromCompactNode over BEncodedString.FromMemory), but
            // saves it back as a bencoded LIST (or "le" when the table is empty). Neither
            // saved form can be reloaded, so always write a fresh flat seed at startup:
            // 20-byte id + 4-byte IPv4 + 2-byte big-endian port, concatenated.
            var nodes = await ResolveRouterNodesAsync(cancellationToken);
            if (nodes.Count == 0)
            {
                AppLogger.Log("DHT bootstrap seed skipped: no router resolved");
                return;
            }

            var file = Path.Combine(cacheDirectory, "dht_nodes.cache");
            using var stream = new MemoryStream(nodes.Count * 26);
            foreach (var compact in nodes)
            {
                stream.Write(compact, 0, compact.Length);
            }

            var blob = stream.ToArray();
            await File.WriteAllBytesAsync(file, blob, cancellationToken);
            AppLogger.Log($"DHT bootstrap cache seeded with {nodes.Count} routers ({blob.Length} bytes flat)");
        }
        catch (Exception exception)
        {
            AppLogger.LogException("DHT cache seeding skipped", exception);
        }
    }

    public static byte[] ToCompactNode(IPAddress address, int port, ReadOnlySpan<byte> nodeId)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (nodeId.Length != 20)
        {
            throw new ArgumentException("Node id must be 20 bytes.", nameof(nodeId));
        }

        var addressBytes = address.GetAddressBytes();
        if (addressBytes.Length != 4)
        {
            throw new ArgumentException("Only IPv4 compact nodes are supported.", nameof(address));
        }

        var compact = new byte[26];
        nodeId.CopyTo(compact.AsSpan(0, 20));
        addressBytes.CopyTo(compact, 20);
        compact[24] = (byte)(port >> 8);
        compact[25] = (byte)port;
        return compact;
    }

    private static async Task<List<byte[]>> ResolveRouterNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<byte[]>();
        foreach (var (host, port) in Routers)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 is null)
                {
                    continue;
                }

                var nodeId = new byte[20];
                RandomNumberGenerator.Fill(nodeId);
                nodes.Add(ToCompactNode(ipv4, port, nodeId));
            }
            catch
            {
                // One dead router must not block the others.
            }
        }

        return nodes;
    }
}
