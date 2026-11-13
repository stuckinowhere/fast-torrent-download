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

    /// <summary>
    /// The app's own listener, started through the real InitializeAsync with default-style settings
    /// (0.0.0.0, IPv6 on): an outside seeder dials in over IPv6 and the app must count it as an
    /// incoming IPv6 peer and receive data. Uses the machine's routable IPv6 address when it has one.
    /// </summary>
    public static async Task RunInboundAsync()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Console.WriteLine("SKIP inbound IPv6 through the app listener: this machine has no IPv6 stack");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-ipv6in-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            const int pieceLength = 16_384;
            var data = RandomNumberGenerator.GetBytes(pieceLength * 800);
            var hashes = Enumerable.Range(0, data.Length / pieceLength)
                .SelectMany(index => SHA1.HashData(data.AsSpan(index * pieceLength, pieceLength))).ToArray();
            var source = Path.Combine(root, "inbound.torrent");
            await File.WriteAllBytesAsync(source, new BEncodedDictionary
            {
                ["info"] = new BEncodedDictionary
                {
                    ["name"] = new BEncodedString("inbound.bin"), ["length"] = new BEncodedNumber(data.Length),
                    ["piece length"] = new BEncodedNumber(pieceLength), ["pieces"] = new BEncodedString(hashes)
                }
            }.Encode());
            var seedFolder = Path.Combine(root, "seed");
            Directory.CreateDirectory(seedFolder);
            await File.WriteAllBytesAsync(Path.Combine(seedFolder, "inbound.bin"), data);

            int FreePort(IPAddress address)
            {
                using var probe = new TcpListener(address, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }

            var appPort = FreePort(IPAddress.IPv6Any);
            var paths = new AppPaths(Path.Combine(root, "app-state"));
            paths.EnsureDirectories();
            await using var service = new TorrentEngineService(paths);
            await service.InitializeAsync(new AppSettings
            {
                DefaultDownloadFolder = Path.Combine(root, "download"), ListenPort = appPort, ListenAddress = "0.0.0.0", EnableIPv6 = true,
                EnableDht = false, EnablePeerExchange = false, EnableLocalPeerDiscovery = false, EnablePortForwarding = false,
                MaximumDownloadKiBPerSecond = 512
            });

            // Prefer a real interface address (ULA or global); fall back to loopback when the machine has none.
            var routable = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address))
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                           !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal && !address.IsIPv6Teredo);
            var target = routable ?? IPAddress.IPv6Loopback;

            var preview = await service.PrepareAddAsync(source, null, timeout.Token);
            await service.CommitAddAsync(preview, ["inbound.bin"], startImmediately: true);

            using var seedEngine = new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(root, "seed-cache"), DhtEndPoint = null, AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv6"] = new(IPAddress.IPv6Loopback, FreePort(IPAddress.IPv6Loopback)) }
            }.ToSettings());
            var seed = await seedEngine.AddAsync(Torrent.Load(source), seedFolder, new TorrentSettingsBuilder
            {
                AllowDht = false, AllowPeerExchange = false, CreateContainingDirectory = false
            }.ToSettings());
            await seed.HashCheckAsync(autoStart: true);
            await UntilAsync(() => seed.State == TorrentState.Seeding);
            while ((await service.GetSnapshotsAsync()).Single().State != "Downloading") await Task.Delay(50, timeout.Token);

            // MonoTorrent opens its listeners once a torrent runs. Both families must then accept a TCP connection.
            using (var dialV4 = new TcpClient(AddressFamily.InterNetwork))
            {
                await dialV4.ConnectAsync(IPAddress.Loopback, appPort, timeout.Token);
            }

            try
            {
                using var dial = new TcpClient(AddressFamily.InterNetworkV6);
                await dial.ConnectAsync(target, appPort, timeout.Token);
            }
            catch (SocketException exception)
            {
                throw new InvalidOperationException($"IPv4 accepts connections on port {appPort}, but nothing listens over IPv6 at [{(routable is null ? "::1" : "interface address")}]: {exception.SocketErrorCode}.");
            }

            // The seeder dials the app: from the app's side this is an incoming connection.
            await seed.AddPeerAsync(new PeerInfo(new Uri($"ipv6://[{target}]:{appPort}")));

            TorrentSnapshot snapshot;
            do
            {
                await Task.Delay(100, timeout.Token);
                snapshot = (await service.GetSnapshotsAsync()).Single();
            }
            while (snapshot.Diagnostics?.IncomingPeers is not > 0 || snapshot.VerifiedBytes < pieceLength * 4);

            Require(snapshot.Diagnostics!.IncomingPeers == 1 && snapshot.Diagnostics.ConnectedSeeders == 1, "The incoming IPv6 seeder is not counted.");
            var seedPeers = await seed.GetPeersAsync();
            Require(seedPeers.Any(peer => peer.IsConnected && peer.Uri.Scheme == "ipv6"), "The seeder is not connected to the app over IPv6.");
            await service.RemoveAsync(snapshot.Id);
            Console.WriteLine($"PASS inbound IPv6 through the app's own listener ({(routable is null ? "loopback ::1" : "interface address, " + (target.IsIPv6UniqueLocal ? "unique-local" : "global"))}): counted as incoming and data received");
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
