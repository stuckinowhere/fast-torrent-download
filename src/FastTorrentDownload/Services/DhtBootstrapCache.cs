using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MonoTorrent.BEncoding;

namespace FastTorrentDownload.Services;

public static class DhtBootstrapCache
{
    private const int CompactNodeBytes = 26;
    private const int MaxSavedNodes = 256;

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
            // saves it back on shutdown as a bencoded LIST of 26-byte strings ("l26:...e").
            // Unwrap any saved bencoded list back into flat 26-byte nodes and append the
            // DNS bootstrap routers so warm DHT routing tables survive restarts.
            var file = Path.Combine(cacheDirectory, "dht_nodes.cache");
            var preserved = new List<byte[]>();
            var seenEndpoints = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(file))
            {
                try
                {
                    var existing = await File.ReadAllBytesAsync(file, cancellationToken);
                    foreach (var node in ExtractSavedCompactNodes(existing))
                    {
                        if (seenEndpoints.Add(EndpointKey(node)) && preserved.Count < MaxSavedNodes)
                        {
                            preserved.Add(node);
                        }
                    }
                }
                catch
                {
                    // Corrupt cache files are replaced cleanly below.
                }
            }

            var routers = await ResolveRouterNodesAsync(cancellationToken);
            foreach (var router in routers)
            {
                if (seenEndpoints.Add(EndpointKey(router)))
                {
                    preserved.Add(router);
                }
            }

            if (preserved.Count == 0)
            {
                AppLogger.Log("DHT bootstrap seed skipped: no nodes available");
                return;
            }

            using var stream = new MemoryStream(preserved.Count * CompactNodeBytes);
            foreach (var compact in preserved)
            {
                stream.Write(compact, 0, compact.Length);
            }

            var blob = stream.ToArray();
            await File.WriteAllBytesAsync(file, blob, cancellationToken);
            AppLogger.Log($"DHT bootstrap cache ready with {preserved.Count} nodes ({routers.Count} routers, {blob.Length} bytes flat)");
        }
        catch (Exception exception)
        {
            AppLogger.LogException("DHT cache seeding skipped", exception);
        }
    }

    public static IReadOnlyList<byte[]> ExtractSavedCompactNodes(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var results = new List<byte[]>();
        if (raw.Length >= 2 && raw[0] == (byte)'l' && raw[^1] == (byte)'e')
        {
            try
            {
                var list = BEncodedValue.Decode<BEncodedList>(raw);
                foreach (var item in list.OfType<BEncodedString>())
                {
                    SliceCompactNodes(item.Span, results);
                }

                return results;
            }
            catch
            {
                // Fall back to flat slicing if the payload merely starts/ends with 'l'/'e' bytes.
            }
        }

        if (raw.Length >= CompactNodeBytes && raw.Length % CompactNodeBytes == 0)
        {
            SliceCompactNodes(raw, results);
        }

        return results;
    }

    private static void SliceCompactNodes(ReadOnlySpan<byte> span, List<byte[]> destination)
    {
        if (span.Length < CompactNodeBytes || span.Length % CompactNodeBytes != 0)
        {
            return;
        }

        for (var offset = 0; offset < span.Length; offset += CompactNodeBytes)
        {
            var slice = span.Slice(offset, CompactNodeBytes);
            // Skip invalid port 0 entries.
            if (slice[24] == 0 && slice[25] == 0)
            {
                continue;
            }

            destination.Add(slice.ToArray());
        }
    }

    private static string EndpointKey(ReadOnlySpan<byte> compactNode) =>
        Convert.ToHexString(compactNode.Slice(20, 6));

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

        var compact = new byte[CompactNodeBytes];
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
