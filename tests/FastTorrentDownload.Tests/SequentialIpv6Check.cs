using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

/// <summary>
/// A real transfer from a seeder that only listens on IPv6 loopback, with in-order picking on:
/// proves an IPv6 peer connection works end to end and that pieces arrive front to back.
/// </summary>
internal static class SequentialIpv6Check
{
    public static async Task RunAsync()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Console.WriteLine("SKIP in-order transfer over IPv6 loopback: this machine has no IPv6 stack");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-ipv6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            const int pieceLength = 16_384;
            var data = RandomNumberGenerator.GetBytes(pieceLength * 400);
            var hashes = Enumerable.Range(0, data.Length / pieceLength)
                .SelectMany(index => SHA1.HashData(data.AsSpan(index * pieceLength, pieceLength))).ToArray();
            var source = Path.Combine(root, "ordered.torrent");
            await File.WriteAllBytesAsync(source, new BEncodedDictionary
            {
                ["info"] = new BEncodedDictionary
                {
                    ["name"] = new BEncodedString("ordered.bin"), ["length"] = new BEncodedNumber(data.Length),
                    ["piece length"] = new BEncodedNumber(pieceLength), ["pieces"] = new BEncodedString(hashes)
                }
            }.Encode());
            var seedFolder = Path.Combine(root, "seed");
            Directory.CreateDirectory(seedFolder);
            await File.WriteAllBytesAsync(Path.Combine(seedFolder, "ordered.bin"), data);

            using var probe = new TcpListener(IPAddress.IPv6Loopback, 0);
            probe.Start();
            var seedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using var seedEngine = new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(root, "seed-cache"), DhtEndPoint = null, AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv6"] = new(IPAddress.IPv6Loopback, seedPort) }
            }.ToSettings());
            var seed = await seedEngine.AddAsync(Torrent.Load(source), seedFolder, new TorrentSettingsBuilder
            {
                AllowDht = false, AllowPeerExchange = false, CreateContainingDirectory = false
            }.ToSettings());
            await seed.HashCheckAsync(autoStart: true);
            await UntilAsync(() => seed.State == TorrentState.Seeding);

            var paths = new AppPaths(Path.Combine(root, "download-state"));
            paths.EnsureDirectories();
            using var downloadEngine = new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = paths.CacheDirectory, DhtEndPoint = null, AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
                MaximumDownloadRate = 512 * 1024, ListenEndPoints = new Dictionary<string, IPEndPoint>()
            }.ToSettings());
            var service = new TorrentEngineService(paths);
            typeof(TorrentEngineService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, downloadEngine);
            var preview = await service.PrepareAddAsync(source, Path.Combine(root, "download"), timeout.Token);
            Require(!preview.Sequential, "In-order downloading must be off unless the user or the default setting asks for it.");
            await service.CommitAddAsync(preview, ["ordered.bin"], startImmediately: true, sequential: true);
            var manager = downloadEngine.Torrents.Single();
            Require((await service.GetSnapshotsAsync()).Single().Sequential, "The in-order flag is missing from the snapshot.");
            // Record the order pieces are verified in; timing-independent, unlike sampling the bitfield.
            var completed = new List<int>();
            manager.PieceHashed += (_, args) =>
            {
                if (args.HashPassed) lock (completed) completed.Add(args.PieceIndex);
            };
            await UntilAsync(() => manager.State is TorrentState.Downloading or TorrentState.Seeding);
            await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv6://[::1]:{seedPort}")));
            await UntilAsync(() => { lock (completed) return completed.Count >= 300; });

            int[] order;
            lock (completed) order = completed.Take(300).ToArray();
            // In-order picking: the k-th verified piece sits near index k. A few stragglers are normal (a request
            // that a peer answers late); rarest-first or random picking would scatter across all 400 pieces.
            var near = order.Where((piece, position) => Math.Abs(piece - position) <= 48).Count();
            var ascending = order.Zip(order.Skip(1), (first, second) => second > first).Count(isAscending => isAscending);
            Require(near >= 270 && ascending >= 255,
                $"Pieces did not arrive in order: {near}/300 near their index, {ascending}/299 ascending steps. First 40: {string.Join(',', order.Take(40))}");
            var peers = await manager.GetPeersAsync();
            Require(peers.Any(peer => peer.IsConnected && peer.Uri.Scheme == "ipv6"), "The peer connection was not made over IPv6.");

            // No snapshot from here on: a refresh would auto-remove the torrent once it completes.
            Require((await service.GetFilesAsync(preview.Id)).Sequential, "The in-order flag is not reported back.");
            var saved = BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(paths.EngineStateFile));
            Require(((BEncodedList)saved["SequentialInfoHashes"]).Count == 1, "The in-order flag was not persisted.");
            // Switching while the torrent is running: it is stopped, switched and started again.
            await service.SetSelectedFilesAsync(preview.Id, ["ordered.bin"], sequential: false);
            Require(manager.State is not (TorrentState.Stopped or TorrentState.Error), $"Switching the picker left the torrent {manager.State}.");
            Require(!(await service.GetFilesAsync(preview.Id)).Sequential, "Turning in-order downloading off did not stick.");
            saved = BEncodedValue.Decode<BEncodedDictionary>(await File.ReadAllBytesAsync(paths.EngineStateFile));
            Require(((BEncodedList)saved["SequentialInfoHashes"]).Count == 0, "Turning in-order downloading off was not persisted.");
            await service.RemoveAsync(preview.Id);
            Console.WriteLine($"PASS in-order transfer over IPv6 loopback: {near}/300 pieces verified near their index, {ascending}/299 ascending steps");
        }
        finally
        {
            timeout.Cancel();
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        async Task UntilAsync(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(25, timeout.Token);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
