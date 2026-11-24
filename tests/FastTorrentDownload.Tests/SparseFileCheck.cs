using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FastTorrentDownload.Models;
using FastTorrentDownload.Services;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

/// <summary>
/// Large payload files must be created sparse. A preallocated, non-sparse file makes NTFS zero
/// everything before the first block written, which stalled the first minutes of a real download.
/// </summary>
internal static class SparseFileCheck
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-sparse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            // Through the app's own engine: payload files are sparse, nothing is zero-filled, and the data is intact.
            const int pieceLength = 262_144;
            var big = RandomNumberGenerator.GetBytes(16 * 1024 * 1024 + 300_123);
            var small = RandomNumberGenerator.GetBytes(100_123);
            var data = big.Concat(small).ToArray();
            var hashes = Enumerable.Range(0, (data.Length + pieceLength - 1) / pieceLength)
                .SelectMany(index => SHA1.HashData(data.AsSpan(index * pieceLength, Math.Min(pieceLength, data.Length - index * pieceLength)))).ToArray();
            var source = Path.Combine(root, "sparse.torrent");
            await File.WriteAllBytesAsync(source, new BEncodedDictionary
            {
                ["info"] = new BEncodedDictionary
                {
                    ["name"] = new BEncodedString("sparse-check"),
                    ["files"] = new BEncodedList
                    {
                        new BEncodedDictionary { ["length"] = new BEncodedNumber(big.Length), ["path"] = new BEncodedList { new BEncodedString("big.bin") } },
                        new BEncodedDictionary { ["length"] = new BEncodedNumber(small.Length), ["path"] = new BEncodedList { new BEncodedString("small.bin") } }
                    },
                    ["piece length"] = new BEncodedNumber(pieceLength), ["pieces"] = new BEncodedString(hashes)
                }
            }.Encode());
            var seedFolder = Path.Combine(root, "seed");
            Directory.CreateDirectory(Path.Combine(seedFolder, "sparse-check"));
            await File.WriteAllBytesAsync(Path.Combine(seedFolder, "sparse-check", "big.bin"), big);
            await File.WriteAllBytesAsync(Path.Combine(seedFolder, "sparse-check", "small.bin"), small);

            int FreePort()
            {
                using var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }

            var appPort = FreePort();
            var paths = new AppPaths(Path.Combine(root, "app-state"));
            paths.EnsureDirectories();
            await using var service = new TorrentEngineService(paths);
            await service.InitializeAsync(new AppSettings
            {
                DefaultDownloadFolder = Path.Combine(root, "download"), ListenPort = appPort, ListenAddress = "127.0.0.1",
                EnableDht = false, EnablePeerExchange = false, EnableLocalPeerDiscovery = false, EnablePortForwarding = false
            });
            var preview = await service.PrepareAddAsync(source, null, timeout.Token);
            await service.CommitAddAsync(preview, preview.Files.Select(file => file.Path).ToArray(), startImmediately: true);

            using var seedEngine = new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(root, "seed-cache"), DhtEndPoint = null, AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, FreePort()) }
            }.ToSettings());
            var seed = await seedEngine.AddAsync(Torrent.Load(source), seedFolder, new TorrentSettingsBuilder
            {
                AllowDht = false, AllowPeerExchange = false, CreateContainingDirectory = true
            }.ToSettings());
            await seed.HashCheckAsync(autoStart: true);
            while (seed.State != TorrentState.Seeding) await Task.Delay(25, timeout.Token);
            while ((await service.GetSnapshotsAsync()).Single().State != "Downloading") await Task.Delay(50, timeout.Token);
            await seed.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{appPort}")));

            // A finished torrent leaves the queue on its own.
            while ((await service.GetSnapshotsAsync()).Count > 0) await Task.Delay(100, timeout.Token);
            var bigPath = Path.Combine(root, "download", "sparse-check", "big.bin");
            var smallPath = Path.Combine(root, "download", "sparse-check", "small.bin");
            Require(File.GetAttributes(bigPath).HasFlag(FileAttributes.SparseFile), "The large payload file was not created sparse.");
            Require((await File.ReadAllBytesAsync(bigPath)).AsSpan().SequenceEqual(big), "The large file is corrupt.");
            Require((await File.ReadAllBytesAsync(smallPath)).AsSpan().SequenceEqual(small), "The small file is corrupt.");
            Console.WriteLine("PASS payload files are created sparse, so no zero-fill precedes the first block, and the data is intact");
        }
        finally
        {
            timeout.Cancel();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
